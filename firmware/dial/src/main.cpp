#include <Arduino.h>
#include <Arduino_GFX_Library.h>
#include <Wire.h>
#include <esp_system.h>
#include <esp_timer.h>
#include <cmath>
#include <cstdio>
#include "board_config.h"
#include "guition_st77916.h"
#define DIAL_ISR_ATTR IRAM_ATTR
#include "dial_core.h"
#include "display_diff.h"
#include "display_motion.h"
#include "display_feedback.h"

namespace {
constexpr char FirmwareVersion[] = "0.1.13";
constexpr uint16_t Background = RGB565(13, 18, 27);
constexpr uint16_t Foreground = RGB565(234, 239, 247);
constexpr uint16_t Muted = RGB565(139, 151, 171);
constexpr uint16_t FanAccent = RGB565(163, 217, 54);
constexpr uint16_t PumpAccent = RGB565(77, 184, 255);
constexpr uint16_t Warning = RGB565(255, 194, 102);
constexpr uint16_t Track = RGB565(35, 45, 60);

Arduino_ESP32QSPI bus(Board::LcdCs, Board::LcdSck, Board::LcdD0,
                     Board::LcdD1, Board::LcdD2, Board::LcdD3);
GuitionSt77916 panel(&bus, Board::LcdReset, Board::LcdRotation);
Arduino_Canvas canvas(Board::Width, Board::Height, &panel);
Dial::Session session;
Dial::LineBuffer receiver;
Dial::DirectionPulseDecoder encoder;
Dial::TapRecognizer tap;
portMUX_TYPE encoderMux = portMUX_INITIALIZER_UNLOCKED;
volatile int encoderSteps = 0;
volatile uint32_t encoderEdgeCount = 0, encoderPinStates[4]{};
volatile uint32_t encoderDecodedPositive = 0, encoderDecodedNegative = 0, encoderDroppedSteps = 0;
esp_timer_handle_t encoderTimer = nullptr;
bool encoderReady = false;
bool displayReady = false, touchReady = false, wasConnected = false;
bool needsRender = true;
char deviceId[32], bootId[33];
uint32_t lastHello = 0, lastTouch = 0, lastRotation = 0, lastRender = 0;
uint32_t messagePage = 0;
uint32_t textPageStartedAt = 0;
int accumulatedSteps = 0;
bool lastFresh = false;
Dial::State drawnState;
bool drawnPreview = false, drawnInFlight = false, drawnQueued = false;
bool drawnFeedback = false;
int32_t drawnDesired = 0;
uint16_t* previousFramebuffer = nullptr;
bool previousFramebufferValid = false;
uint32_t maxDrawMs = 0, maxFlushMs = 0, lastFlushPixels = 0, renderCount = 0;
Dial::Display::Motion iconMotion;
Dial::Display::Feedback feedback;
uint32_t animationFrameCount = 0, maxAnimationDrawMs = 0, maxAnimationFlushMs = 0, maxAnimationRenderMs = 0;
uint32_t lastAnimationFlushPixels = 0;
// Observability only: these counters do not influence transport or control.
uint32_t usbFalseCount = 0, stateExpireCount = 0, maxLoopGapMs = 0, maxRenderMs = 0;
uint32_t acceptedStateCount = 0, rejectedFrameCount = 0, identifyCount = 0, rxBytes = 0;
uint32_t lastLoopAt = 0, lastAcceptedStateAt = 0;
bool loopSeen = false, acceptedStateSeen = false;

uint8_t IRAM_ATTR encoderPins() {
    return (digitalRead(Board::EncoderA) ? 2 : 0) | (digitalRead(Board::EncoderB) ? 1 : 0);
}

void IRAM_ATTR encoderInterrupt() {
    portENTER_CRITICAL_ISR(&encoderMux);
    ++encoderEdgeCount;
    ++encoderPinStates[encoderPins()];
    portEXIT_CRITICAL_ISR(&encoderMux);
}

void sampleEncoder(void*) {
    portENTER_CRITICAL(&encoderMux);
    const int step = encoder.update(encoderPins()) * Board::EncoderDirection;
    if (step > 0) ++encoderDecodedPositive;
    if (step < 0) ++encoderDecodedNegative;
    // The independent timer keeps sampling during display transfers.
    const int next = encoderSteps + step;
    if (next >= -20 && next <= 20) encoderSteps = next;
    else ++encoderDroppedSteps;
    portEXIT_CRITICAL(&encoderMux);
}

void discardInput() {
    portENTER_CRITICAL(&encoderMux);
    encoderDroppedSteps += static_cast<uint32_t>(abs(encoderSteps) + abs(accumulatedSteps));
    encoderSteps = 0;
    encoder.setEnabled(false, encoderPins());
    encoder.reset(encoderPins());
    portEXIT_CRITICAL(&encoderMux);
    accumulatedSteps = 0;
    tap.cancel();
}

bool transmit(JsonDocument& frame) {
    // One complete LF-terminated frame per write; no persistent retry queue.
    char buffer[Dial::MaxFrameBytes];
    const size_t length = measureJson(frame);
    if (length + 1 > sizeof(buffer) || !Serial ||
        Serial.availableForWrite() <= static_cast<int>(length + 1)) return false;
    const size_t written = serializeJson(frame, buffer, sizeof(buffer));
    if (written != length) return false;
    buffer[length] = '\n';
    return Serial.write(reinterpret_cast<const uint8_t*>(buffer), length + 1) == length + 1;
}

void hello(uint32_t now) {
    JsonDocument frame;
    frame["v"] = 1;
    frame["type"] = "hello";
    frame["deviceType"] = "FlowDeckDial";
    frame["deviceId"] = deviceId;
    frame["bootId"] = bootId;
    frame["firmwareVersion"] = FirmwareVersion;
    frame["responsiveInput"] = 1;
    auto diagnostics = frame["diagnostics"].to<JsonObject>();
    diagnostics["usbFalseCount"] = usbFalseCount;
    diagnostics["stateExpireCount"] = stateExpireCount;
    diagnostics["maxLoopGapMs"] = maxLoopGapMs;
    diagnostics["maxRenderMs"] = maxRenderMs;
    diagnostics["maxDrawMs"] = maxDrawMs;
    diagnostics["maxFlushMs"] = maxFlushMs;
    diagnostics["lastFlushPixels"] = lastFlushPixels;
    diagnostics["renderCount"] = renderCount;
    diagnostics["animationFrameCount"] = animationFrameCount;
    diagnostics["maxAnimationDrawMs"] = maxAnimationDrawMs;
    diagnostics["maxAnimationFlushMs"] = maxAnimationFlushMs;
    diagnostics["maxAnimationRenderMs"] = maxAnimationRenderMs;
    diagnostics["lastAnimationFlushPixels"] = lastAnimationFlushPixels;
    diagnostics["lcdRotation"] = panel.getRotation();
    diagnostics["partialFlushEnabled"] = previousFramebuffer != nullptr;
    diagnostics["responsiveInput"] = session.responsive();
    diagnostics["acceptedStateCount"] = acceptedStateCount;
    diagnostics["rejectedFrameCount"] = rejectedFrameCount;
    diagnostics["identifyCount"] = identifyCount;
    diagnostics["rxBytes"] = rxBytes;
    if (acceptedStateSeen) diagnostics["lastStateAgeMs"] = now - lastAcceptedStateAt;
    else diagnostics["lastStateAgeMs"] = nullptr;
    diagnostics["sessionWelcomed"] = session.welcomed();
    diagnostics["sessionHasState"] = session.hasState();
    uint32_t edgeCount, pinStates[4], positive, negative, dropped;
    portENTER_CRITICAL(&encoderMux);
    edgeCount = encoderEdgeCount;
    for (unsigned i = 0; i < 4; ++i) pinStates[i] = encoderPinStates[i];
    positive = encoderDecodedPositive;
    negative = encoderDecodedNegative;
    dropped = encoderDroppedSteps;
    portEXIT_CRITICAL(&encoderMux);
    diagnostics["encoderReady"] = encoderReady;
    diagnostics["encoderEdgeCount"] = edgeCount;
    auto histogram = diagnostics["encoderPinStates"].to<JsonArray>();
    for (const auto value : pinStates) histogram.add(value);
    diagnostics["encoderDecodedPositive"] = positive;
    diagnostics["encoderDecodedNegative"] = negative;
    diagnostics["encoderDroppedSteps"] = dropped;
    transmit(frame);
    lastHello = now;
}

bool displayChanged(const Dial::State& a, const Dial::State& b) {
    return a.value != b.value || a.min != b.min || a.max != b.max ||
           a.valueMixed != b.valueMixed || a.memberCount != b.memberCount ||
           a.rpmProvided != b.rpmProvided || a.rpmMin != b.rpmMin || a.rpmMax != b.rpmMax || a.rpmCount != b.rpmCount ||
           a.temperaturesProvided != b.temperaturesProvided ||
           a.cpuTempValid != b.cpuTempValid || a.gpuTempValid != b.gpuTempValid || a.waterTempValid != b.waterTempValid ||
           a.cpuTempDeciC != b.cpuTempDeciC || a.gpuTempDeciC != b.gpuTempDeciC || a.waterTempDeciC != b.waterTempDeciC ||
           a.kind != b.kind || a.index != b.index || a.count != b.count ||
           a.enabled != b.enabled || a.busy != b.busy || a.ackStatus != b.ackStatus ||
           std::strcmp(a.label, b.label) || std::strcmp(a.message, b.message) ||
           std::strcmp(a.targetId, b.targetId);
}

// Bounded number of bytes each pass, so a noisy host cannot starve UI/input.
void receiveFrames(uint32_t now) {
    for (unsigned bytes = 0; bytes < 2048 && Serial.available(); ++bytes) {
        const int next = Serial.read();
        if (next < 0) continue;
        ++rxBytes;
        if (!receiver.push(static_cast<char>(next))) continue;
        const Dial::State previous = session.state();
        const uint32_t cancellation = session.cancellationSerial();
        const uint32_t previousSeq = session.inFlightSeq();
        const bool previousPreview = session.hasPreview(), previousFresh = session.fresh(now);
        const bool previousQueued = session.hasQueuedDesired();
        const int32_t previousDesired = session.desiredValue();
        const auto result = session.receive(receiver.line(), now);
        if (result == Dial::FrameResult::Identify) {
            ++identifyCount;
            // Every handshake discards user input from the previous host session.
            session.disconnect();
            iconMotion.reset();
            feedback.reset();
            discardInput();
            needsRender = true;
            hello(now);
        } else if (result == Dial::FrameResult::Welcome || result == Dial::FrameResult::State) {
            if (result == Dial::FrameResult::Welcome) { iconMotion.reset(); feedback.reset(); }
            if (result == Dial::FrameResult::State) {
                feedback.observe(previous, previousSeq, previousPreview, previousQueued, previousDesired,
                    cancellation, previousFresh, session, now);
                ++acceptedStateCount;
                lastAcceptedStateAt = now;
                acceptedStateSeen = true;
            }
            const auto& current = session.state();
            if (result == Dial::FrameResult::Welcome || session.cancellationSerial() != cancellation ||
                std::strcmp(previous.targetId, current.targetId) ||
                (!session.responsive() && (std::strcmp(previous.stateId, current.stateId) || previous.busy != current.busy)))
                discardInput();
            if (result == Dial::FrameResult::Welcome || displayChanged(drawnState, session.state())) {
                needsRender = true;
                if (result == Dial::FrameResult::Welcome) messagePage = 0;
            }
        } else {
            ++rejectedFrameCount;
        }
    }
}

bool readTouch(bool& down, int& x, int& y) {
    Wire.beginTransmission(Board::TouchAddress);
    Wire.write(0x02); // finger count, X high/low, Y high/low
    if (Wire.endTransmission(false) != 0) return false;
    if (Wire.requestFrom(static_cast<uint8_t>(Board::TouchAddress), static_cast<uint8_t>(5)) != 5) return false;
    uint8_t data[5];
    for (auto& value : data) value = static_cast<uint8_t>(Wire.read());
    const unsigned fingers = data[0] & 0x0f;
    if (fingers > 1) return false;
    down = fingers == 1;
    x = ((data[1] & 0x0f) << 8) | data[2];
    y = ((data[3] & 0x0f) << 8) | data[4];
    return !down || (x < Board::Width && y < Board::Height);
}

void sendInput(bool press, int delta, uint32_t now) {
    if (!displayReady) return;
    JsonDocument frame;
    const bool sent = session.makeInput(press, delta, now, frame) && transmit(frame);
    if (!sent && session.responsive()) session.writeFailed();
    if (!press) {
        portENTER_CRITICAL(&encoderMux);
        if (!sent) encoderDroppedSteps += static_cast<uint32_t>(abs(delta));
        portEXIT_CRITICAL(&encoderMux);
        accumulatedSteps = 0;
    }
    // Input generated during or before this send is never replayed on a new state.
    discardInput();
}

void handleInput(uint32_t now, bool transportReady) {
    const bool canRotate = transportReady && encoderReady && displayReady &&
        (session.responsive() ? session.canAdjust(now) : session.canInput(now));
    const bool canPress = transportReady && displayReady && session.canInput(now, true);
    int steps;
    portENTER_CRITICAL(&encoderMux);
    // The timer must not queue pulses while awaiting a state or otherwise
    // disabled. Re-enabling starts from today's pins and drops any old queue.
    if (!canRotate || encoder.enabled() != canRotate) {
        encoderDroppedSteps += static_cast<uint32_t>(abs(encoderSteps) + abs(accumulatedSteps));
        encoderSteps = 0;
        accumulatedSteps = 0;
        encoder.reset(encoderPins());
    }
    encoder.setEnabled(canRotate, encoderPins());
    steps = encoderSteps;
    encoderSteps = 0;
    portEXIT_CRITICAL(&encoderMux);
    if (canRotate && session.responsive()) {
        if (session.adjustDesired(steps, now)) needsRender = true;
        accumulatedSteps = 0;
    } else if (canRotate) accumulatedSteps = constrain(accumulatedSteps + steps, -20, 20);
    else accumulatedSteps = 0;
    if (!canPress) tap.cancel();

    if (now - lastTouch >= 15) {
        lastTouch = now;
        bool down = false;
        int x = 0, y = 0;
        const bool valid = readTouch(down, x, y);
        if (valid != touchReady) { touchReady = valid; needsRender = true; }
        const bool press = tap.update(down, x, y, now, valid);
        if (press && canPress) { sendInput(true, 0, now); return; }
    }
    if (canRotate && session.responsive() && session.canSendDesired(now) && now - lastRotation >= 40) {
        JsonDocument frame;
        if (session.makeSet(now, frame)) {
            lastRotation = now;
            if (!transmit(frame)) session.writeFailed();
            needsRender = true;
            // Keep sampling during our own operation; only the newest desired
            // absolute value is retained, never a queue of writes.
        }
    } else if (canRotate && !session.responsive() && accumulatedSteps && now - lastRotation >= 40) {
        lastRotation = now;
        sendInput(false, accumulatedSteps, now);
    }
}

void japaneseFont() {
    // JIS X 0208 Japanese coverage supplied by U8g2's GNU Unifont subset.
    canvas.setFont(u8g2_font_unifont_t_japanese3);
    canvas.setUTF8Print(true);
    canvas.setTextSize(1);
    canvas.setTextWrap(false);
}

using DisplayMode = Dial::Display::Mode;
constexpr int IconRadius = 19;
constexpr int IconSize = IconRadius * 2 + 1;

DisplayMode displayMode(const Dial::State& state, bool fresh) {
    // These exact group IDs are supplied by FlowDeck. Labels, RPM and member
    // counts do not identify a device type, and old observations are not live.
    return Dial::Display::mode(state, fresh);
}

void modeIcon(DisplayMode mode, int centerX, int centerY, uint16_t color,
              uint32_t phase = 0, bool moving = false) {
    if (mode == DisplayMode::Fans) {
        // The stationary rim and hub frame four rotating swept blades. Phase is
        // bounded fixed-point; trigonometry here never accumulates timing error.
        canvas.drawCircle(centerX, centerY, 18, color);
        const float angle = phase * (6.28318530718f / 1000000.0f);
        const float cosine = std::cos(angle), sine = std::sin(angle);
        const auto rotateX = [&](int x, int y) {
            return centerX + static_cast<int>(std::lround(x * cosine - y * sine));
        };
        const auto rotateY = [&](int x, int y) {
            return centerY + static_cast<int>(std::lround(x * sine + y * cosine));
        };
        for (unsigned blade = 0; blade < 4; ++blade) {
            const int dx[] = {3, 10, -3, -10};
            const int dy[] = {-10, 3, 10, -3};
            const int ax[] = {0, 2, 0, -2}, ay[] = {-2, 0, 2, 0};
            const int bx[] = {8, 8, -8, -8}, by[] = {-8, 8, 8, -8};
            const int cx[] = {-1, 12, 1, -12}, cy[] = {-12, -1, 12, 1};
            canvas.fillCircle(rotateX(dx[blade], dy[blade]), rotateY(dx[blade], dy[blade]), 6, color);
            canvas.fillTriangle(rotateX(ax[blade], ay[blade]), rotateY(ax[blade], ay[blade]),
                rotateX(bx[blade], by[blade]), rotateY(bx[blade], by[blade]),
                rotateX(cx[blade], cy[blade]), rotateY(cx[blade], cy[blade]), color);
        }
        canvas.fillCircle(centerX, centerY, 5, Background);
        canvas.fillCircle(centerX, centerY, 3, color);
    } else if (mode == DisplayMode::Pumps) {
        canvas.fillTriangle(centerX, centerY - 16, centerX - 11, centerY + 2,
            centerX + 11, centerY + 2, color);
        canvas.fillCircle(centerX, centerY + 4, 11, color);
        if (moving) {
            // The silhouette stays fixed. A curved highlight travels upward
            // inside an inset mask; no light pixel can spill outside the drop.
            const int streamY = 14 - static_cast<int>(phase * 30 / 1000000);
            for (int y = -13; y <= 13; ++y) {
                for (int x = -9; x <= 9; ++x) {
                    const bool inset = (y <= 2 && std::abs(x) * 18 <= 9 * (y + 14)) ||
                        (x * x + (y - 4) * (y - 4) <= 81);
                    const int distance = y - (streamY + x * x / 18);
                    if (inset && distance >= 0 && distance <= 4)
                        canvas.drawPixel(centerX + x, centerY + y,
                            distance <= 1 ? RGB565(178, 231, 255) : RGB565(104, 200, 255));
                }
            }
        }
    }
}

void textTopCentered(const char* text, int centerX, int top, uint16_t color) {
    int16_t x, y;
    uint16_t w, h;
    canvas.getTextBounds(text, 0, 0, &x, &y, &w, &h);
    canvas.setTextColor(color);
    canvas.setCursor(centerX - static_cast<int>(w) / 2 - x, top - y);
    canvas.print(text);
}

void asciiFont(uint8_t scale) {
    canvas.setFont(static_cast<const GFXfont*>(nullptr));
    canvas.setUTF8Print(false);
    canvas.setTextSize(scale);
    canvas.setTextWrap(false);
}

uint8_t fitAsciiFont(const char* text, uint8_t preferred, uint16_t maximumWidth) {
    for (uint8_t scale = preferred; scale > 1; --scale) {
        asciiFont(scale);
        int16_t x, y;
        uint16_t width, height;
        canvas.getTextBounds(text, 0, 0, &x, &y, &width, &height);
        if (width <= maximumWidth) return scale;
    }
    asciiFont(1);
    return 1;
}

void displayValue(int32_t value, bool showValue, uint16_t color, int top = 136, uint8_t scale = 9) {
    if (!showValue) {
        asciiFont(scale);
        textTopCentered("--", 180, top, Muted);
        return;
    }
    char number[4];
    std::snprintf(number, sizeof(number), "%ld", static_cast<long>(value));
    int16_t nx, ny, px, py;
    uint16_t nw, nh, pw, ph;
    asciiFont(scale);
    canvas.getTextBounds(number, 0, 0, &nx, &ny, &nw, &nh);
    asciiFont(3);
    canvas.getTextBounds("%", 0, 0, &px, &py, &pw, &ph);
    const int left = (Board::Width - static_cast<int>(nw + pw) - 8) / 2;
    canvas.setTextColor(color);
    asciiFont(scale);
    canvas.setCursor(left - nx, top - ny);
    canvas.print(number);
    asciiFont(3);
    canvas.setCursor(left + nw + 8 - px, top + nh - ph - py);
    canvas.print("%");
}

void displayTemperature(const char* label, int32_t value, bool valid, bool decimal, int centerX) {
    japaneseFont();
    int16_t lx, ly, cx, cy;
    uint16_t labelWidth, labelHeight, unitWidth, unitHeight;
    canvas.getTextBounds(label, 0, 0, &lx, &ly, &labelWidth, &labelHeight);
    canvas.getTextBounds("C", 0, 0, &cx, &cy, &unitWidth, &unitHeight);
    const int left = centerX - static_cast<int>(labelWidth + 16 + unitWidth) / 2;
    canvas.setTextColor(Muted);
    canvas.setCursor(left - lx, 214 - ly);
    canvas.print(label);
    // The Japanese subset need not contain U+00B0. Draw a visible degree mark
    // ourselves, then the C, rather than silently depending on that glyph.
    const int degreeX = left + labelWidth + 10;
    canvas.drawCircle(degreeX, 216, 1, Muted);
    canvas.setCursor(left + labelWidth + 16 - cx, 214 - cy);
    canvas.print("C");
    char number[8];
    if (!valid) std::snprintf(number, sizeof(number), "--");
    else if (decimal) std::snprintf(number, sizeof(number), "%ld.%ld",
        static_cast<long>(value / 10), static_cast<long>(value % 10));
    else std::snprintf(number, sizeof(number), "%ld", static_cast<long>((value + 5) / 10));
    // Keep the unit in the small heading so even 150.0 fits at the larger size.
    fitAsciiFont(number, 3, decimal ? 92 : 78);
    textTopCentered(number, centerX, 236, valid ? Foreground : Muted);
}

void settingMarker(int value) {
    const float angle = 135.0f + value * 2.7f;
    // A narrow tick denotes a setting bound; it never clamps the observation.
    canvas.fillArc(180, 180, 167, 142, angle - 0.4f, angle + 0.4f, Foreground);
}

// Wrap unknown target labels on UTF-8 codepoint boundaries into bounded pages.
// Truncation never splits a glyph.
unsigned wrapText(const char* text, char (&lines)[20][193], int width) {
    unsigned count = 0;
    size_t used = 0;
    lines[0][0] = '\0';
    for (size_t i = 0; text[i] && count < 20;) {
        const uint8_t first = static_cast<uint8_t>(text[i]);
        const size_t bytes = first < 0x80 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        char candidate[193];
        if (used + bytes >= sizeof(candidate)) break;
        std::memcpy(candidate, lines[count], used);
        std::memcpy(candidate + used, text + i, bytes);
        candidate[used + bytes] = '\0';
        int16_t bx, by;
        uint16_t w, h;
        canvas.getTextBounds(candidate, 0, 0, &bx, &by, &w, &h);
        if (w > width && used) {
            if (++count >= 20) break;
            used = 0;
            lines[count][0] = '\0';
            continue;
        }
        used += bytes;
        std::memcpy(lines[count], candidate, used + 1);
        i += bytes;
    }
    return count < 20 ? count + 1 : 20;
}

struct WrappedText {
    char source[257]{};
    char lines[20][193]{};
    unsigned count = 0;
    int width = 0;
};
WrappedText labelText;
const WrappedText& cachedText(WrappedText& cache, const char* text, int width) {
    if (!cache.count || cache.width != width || std::strcmp(cache.source, text)) {
        std::snprintf(cache.source, sizeof(cache.source), "%s", text);
        cache.count = wrapText(cache.source, cache.lines, width);
        cache.width = width;
    }
    return cache;
}

void flushDisplay() {
    const uint32_t started = millis();
    uint16_t* pixels = canvas.getFramebuffer();
    if (previousFramebuffer) {
        lastFlushPixels = Dial::changedBands(pixels, previousFramebufferValid ? previousFramebuffer : nullptr,
            Board::Width, Board::Height, 16, [&](unsigned row, unsigned rows) {
                panel.draw16bitRGBBitmap(0, row, pixels + row * Board::Width, Board::Width, rows);
            });
        std::memcpy(previousFramebuffer, pixels, Board::Width * Board::Height * sizeof(uint16_t));
        previousFramebufferValid = true;
    } else {
        canvas.flush();
        lastFlushPixels = Board::Width * Board::Height;
    }
    const uint32_t elapsed = millis() - started;
    if (elapsed > maxFlushMs) maxFlushMs = elapsed;
}

void renderIconOnly(DisplayMode mode, int centerY, uint16_t color) {
    const uint32_t drawStarted = millis();
    const int left = 180 - IconRadius, top = centerY - IconRadius;
    canvas.fillRect(left, top, IconSize, IconSize, Background);
    modeIcon(mode, 180, centerY, color, iconMotion.phase(), iconMotion.active());
    const uint32_t drawElapsed = millis() - drawStarted;
    if (drawElapsed > maxAnimationDrawMs) maxAnimationDrawMs = drawElapsed;

    const uint32_t flushStarted = millis();
    // The canvas has a 360-pixel row stride. Pack this isolated 39x39 region
    // before transmitting it, without redrawing or scanning the rest of the UI.
    static uint16_t iconPixels[IconSize * IconSize];
    uint16_t* pixels = canvas.getFramebuffer();
    for (int row = 0; row < IconSize; ++row) {
        const unsigned offset = (top + row) * Board::Width + left;
        std::memcpy(iconPixels + row * IconSize, pixels + offset, IconSize * sizeof(uint16_t));
        // Keep the next normal band-diff frame exact after any animation count.
        if (previousFramebuffer && previousFramebufferValid)
            std::memcpy(previousFramebuffer + offset, pixels + offset, IconSize * sizeof(uint16_t));
    }
    panel.draw16bitRGBBitmap(left, top, iconPixels, IconSize, IconSize);
    lastAnimationFlushPixels = lastFlushPixels = IconSize * IconSize;
    const uint32_t flushElapsed = millis() - flushStarted;
    if (flushElapsed > maxAnimationFlushMs) maxAnimationFlushMs = flushElapsed;
    if (flushElapsed > maxFlushMs) maxFlushMs = flushElapsed;
    const uint32_t renderElapsed = millis() - drawStarted;
    if (renderElapsed > maxAnimationRenderMs) maxAnimationRenderMs = renderElapsed;
    ++animationFrameCount;
    ++renderCount;
}

void rasterMeterArc(float end, uint16_t color, bool dashed) {
    if (!dashed) { canvas.fillArc(180, 180, 164, 146, 135.0f, end, color); return; }
    for (float start = 135.0f; start < end; start += 10.0f) {
        const float dashEnd = start + 6.0f < end ? start + 6.0f : end;
        canvas.fillArc(180, 180, 164, 146, start, dashEnd, color);
    }
}

struct ArcGapSpan { uint16_t x, y, width; };
constexpr unsigned ArcGapCapacity = 1024;
ArcGapSpan arcGaps[ArcGapCapacity];
unsigned arcGapCount = 0;
bool arcGapsReady = false;

void prepareArcGaps() {
    if (arcGapsReady) return;
    // Build the exact inverse of Arduino_GFX's dashed arc once, during display
    // initialization before controls are enabled. This reuses its edge rules
    // rather than introducing a second rasterizer or allocating more PSRAM.
    arcGapCount = 0;
    canvas.fillScreen(Background);
    rasterMeterArc(405.0f, Foreground, false);
    rasterMeterArc(405.0f, Track, true);
    const uint16_t* pixels = canvas.getFramebuffer();
    bool complete = true;
    for (unsigned y = 0; y < Board::Height; ++y) {
        for (unsigned x = 0; x < Board::Width;) {
            if (pixels[y * Board::Width + x] != Foreground) { ++x; continue; }
            const unsigned start = x;
            while (x < Board::Width && pixels[y * Board::Width + x] == Foreground) ++x;
            if (arcGapCount < ArcGapCapacity)
                arcGaps[arcGapCount++] = {static_cast<uint16_t>(start), static_cast<uint16_t>(y),
                    static_cast<uint16_t>(x - start)};
            else complete = false;
        }
    }
    arcGapsReady = complete && arcGapCount > 0;
    canvas.fillScreen(Background);
}

void clearArcGaps() {
    for (unsigned i = 0; i < arcGapCount; ++i) {
        const auto& gap = arcGaps[i];
        canvas.drawFastHLine(gap.x, gap.y, gap.width, Background);
    }
}

float dashedArcEnd(float end) {
    const unsigned segment = static_cast<unsigned>((end - 135.0f) / 10.0f);
    const float lastDashEnd = 135.0f + segment * 10.0f + 6.0f;
    // An endpoint inside a gap must stop at the previous dash. Otherwise the
    // rasterizer's edge coverage can reach pixels in the following dash.
    return end < lastDashEnd ? end : lastDashEnd;
}

void meterArc(float end, uint16_t color, bool dashed) {
    rasterMeterArc(dashed && arcGapsReady ? dashedArcEnd(end) : end, color, dashed && !arcGapsReady);
    if (dashed && arcGapsReady) clearArcGaps();
}

void successCheck(int top, uint16_t color) {
    // Draw the check ourselves; no font coverage assumption for this symbol.
    char count[16];
    std::snprintf(count, sizeof(count), "%ld/%ld", static_cast<long>(feedback.members()),
        static_cast<long>(feedback.members()));
    asciiFont(2);
    int16_t x, y;
    uint16_t w, h;
    canvas.getTextBounds(count, 0, 0, &x, &y, &w, &h);
    const int left = 180 - (static_cast<int>(w) + 28) / 2;
    for (int offset = 0; offset < 3; ++offset) {
        canvas.drawLine(left, top + 7 + offset, left + 6, top + 13 + offset, color);
        canvas.drawLine(left + 6, top + 13 + offset, left + 19, top + offset, color);
    }
    canvas.setTextColor(color);
    canvas.setCursor(left + 28 - x, top - y);
    canvas.print(count);
}

void render(uint32_t now) {
    if (!displayReady) return;
    const bool fresh = session.fresh(now);
    const auto& state = session.state();
    const bool preview = fresh && session.hasPreview();
    const int32_t value = preview ? session.desiredValue() : state.value;
    if (preview != drawnPreview || value != drawnDesired || session.inFlight() != drawnInFlight ||
        session.hasQueuedDesired() != drawnQueued) needsRender = true;
    feedback.update(session, now);
    if (feedback.visible() != drawnFeedback) needsRender = true;
    // Unknown target labels retain their bounded pages. Explanatory host
    // messages are represented by short state-driven notifications instead.
    if (fresh != lastFresh || std::strcmp(state.label, drawnState.label) ||
        std::strcmp(state.targetId, drawnState.targetId)) textPageStartedAt = now;
    const uint32_t page = (now - textPageStartedAt) / 4000;
    if (page != messagePage) { messagePage = page; needsRender = true; }
    if (fresh != lastFresh) { needsRender = true; lastFresh = fresh; }
    iconMotion.update(state, fresh, now);
    if ((!needsRender && !iconMotion.active()) || now - lastRender < 40) return;
    lastRender = now;
    if (!needsRender) {
        const DisplayMode mode = iconMotion.currentMode();
        renderIconOnly(mode, fresh && state.temperaturesProvided ? 61 : 67,
            mode == DisplayMode::Fans ? FanAccent : PumpAccent);
        return;
    }
    needsRender = false;
    drawnState = state;
    drawnPreview = preview; drawnDesired = value;
    drawnInFlight = session.inFlight(); drawnQueued = session.hasQueuedDesired();
    drawnFeedback = feedback.visible();
    const uint32_t drawStarted = millis();

    canvas.fillScreen(Background);
    const bool showValue = fresh && state.count > 0;
    const bool unverified = showValue && state.ackStatus == Dial::AckStatus::Unverified &&
        !session.hasPreview() && !session.inFlight();
    const bool showOutput = showValue && !unverified;
    const bool showRpm = showValue && state.rpmProvided;
    const bool showTemperatures = fresh && state.temperaturesProvided;
    const DisplayMode mode = displayMode(state, fresh);
    const uint16_t modeColor = mode == DisplayMode::Fans ? FanAccent :
                               mode == DisplayMode::Pumps ? PumpAccent : Muted;
    // Color identifies the selected group. Broken arcs distinguish requests
    // awaiting completion; only a matching confirmed set can show a check.
    const uint16_t valueColor = Foreground;
    const uint16_t arcColor = modeColor;
    const bool pending = Dial::Display::pending(session, fresh);
    // In screen coordinates, 135 -> 405 degrees travels clockwise from the
    // lower left around the top to the lower right, leaving a 90-degree gap.
    // Two solid rasterizations plus small cached gap spans replace up to 54
    // repeated annulus scans. A failed cache keeps the exact, slower fallback.
    const bool cachedDashes = pending && arcGapsReady;
    meterArc(405.0f, Track, pending && !cachedDashes);
    if (showOutput && (!state.valueMixed || preview) && value > 0) {
        const float end = 135.0f + value * 2.7f;
        meterArc(cachedDashes ? dashedArcEnd(end) : end, arcColor, pending && !cachedDashes);
    }
    if (cachedDashes) clearArcGaps();
    if (showOutput && state.valueMixed && !preview) {
        // The marker is the next rotation's basis, not a common confirmed duty.
        const float basis = 135.0f + state.value * 2.7f;
        const float start = basis < 136.2f ? 135.0f : basis - 1.2f;
        const float end = basis > 403.8f ? 405.0f : basis + 1.2f;
        canvas.fillArc(180, 180, 164, 146, start, end, Muted);
    }
    if (showValue && state.min > 0) settingMarker(state.min);
    if (showValue && state.max < 100 && state.max != state.min) settingMarker(state.max);

    if (mode == DisplayMode::Neutral) {
        asciiFont(2);
        textTopCentered("FlowDeck", 180, showTemperatures ? 52 : 60, Muted);
    }
    japaneseFont();

    if (mode != DisplayMode::Neutral) {
        modeIcon(mode, 180, showTemperatures ? 61 : 67, modeColor, iconMotion.phase(), iconMotion.active());
        char title[40];
        std::snprintf(title, sizeof(title), "%s %ld台",
            mode == DisplayMode::Fans ? "全ファン" : "ポンプ", static_cast<long>(state.memberCount));
        textTopCentered(title, 180, showTemperatures ? 87 : 96, modeColor);
    } else if (showValue) {
        const auto& wrapped = cachedText(labelText, state.label[0] ? state.label : "操作対象", showTemperatures ? 192 : 208);
        const unsigned pages = (wrapped.count + 1) / 2;
        const unsigned start = (page % pages) * 2;
        textTopCentered(wrapped.lines[start], 180, showTemperatures ? 73 : showRpm ? 82 : 87, Foreground);
        if (start + 1 < wrapped.count) textTopCentered(wrapped.lines[start + 1], 180, showTemperatures ? 91 : showRpm ? 101 : 106, Foreground);
    }
    if (showOutput && state.valueMixed && !preview) {
        japaneseFont(); canvas.setTextSize(3);
        textTopCentered("混在", 180, showTemperatures ? 116 : showRpm ? 129 : 143, valueColor);
    } else displayValue(value, showOutput, valueColor, showTemperatures ? 109 : showRpm ? 122 : 136,
        showTemperatures ? 8 : 9);
    if (showRpm || showTemperatures) {
        char rpm[48];
        if (!showRpm) std::snprintf(rpm, sizeof(rpm), "-- RPM");
        else if (state.rpmCount < state.memberCount) std::snprintf(rpm, sizeof(rpm), "-- RPM (%ld/%ld)",
            static_cast<long>(state.rpmCount), static_cast<long>(state.memberCount));
        else if (state.rpmMin == state.rpmMax) std::snprintf(rpm, sizeof(rpm), "%ld RPM", static_cast<long>(state.rpmMin));
        else std::snprintf(rpm, sizeof(rpm), "%ld-%ld RPM", static_cast<long>(state.rpmMin), static_cast<long>(state.rpmMax));
        // Long ranges and partial-group counts remain complete within the arc.
        fitAsciiFont(rpm, showTemperatures ? 3 : 2, 272);
        textTopCentered(rpm, 180, showTemperatures ? 178 : 196,
            showRpm && state.rpmCount == state.memberCount ? Foreground : Muted);
    }
    if (showTemperatures) {
        displayTemperature("CPU", state.cpuTempDeciC, state.cpuTempValid, false, 90);
        displayTemperature("GPU", state.gpuTempDeciC, state.gpuTempValid, false, 174);
        displayTemperature("水温", state.waterTempDeciC, state.waterTempValid, true, 258);
    }
    const char* status = Dial::Display::shortStatus(session, fresh, touchReady);
    const int feedbackTop = showTemperatures ? 278 : 250;
    if (status[0]) {
        japaneseFont();
        textTopCentered(status, 180, feedbackTop + 3, Warning);
    } else if (pending) {
        for (int x = 171; x <= 189; x += 9) canvas.fillCircle(x, feedbackTop + 10, 2, Muted);
    } else if (feedback.visible()) successCheck(feedbackTop, modeColor);
    japaneseFont();
    textTopCentered("0", 84, 288, Muted);
    textTopCentered("100", 276, 288, Muted);
    if (fresh && state.count == 2) {
        canvas.fillCircle(173, 310, 3, state.index == 1 ? modeColor : Track);
        canvas.fillCircle(187, 310, 3, state.index == 2 ? modeColor : Track);
    } else if (fresh && state.count > 2) {
        char position[24];
        std::snprintf(position, sizeof(position), "%ld/%ld", static_cast<long>(state.index), static_cast<long>(state.count));
        asciiFont(1);
        textTopCentered(position, 180, 307, Muted);
    }
    const uint32_t drawElapsed = millis() - drawStarted;
    if (drawElapsed > maxDrawMs) maxDrawMs = drawElapsed;
    flushDisplay();
    ++renderCount;
}
} // namespace

void setup() {
    Serial.setRxBufferSize(8192);
    Serial.setTxBufferSize(8192);
    Serial.setTxTimeoutMs(0);
    Serial.begin(115200);
    Serial.setDebugOutput(false);
    const uint64_t mac = ESP.getEfuseMac();
    std::snprintf(deviceId, sizeof(deviceId), "dial-%04lx%08lx",
                  static_cast<unsigned long>(mac >> 32), static_cast<unsigned long>(mac));
    std::snprintf(bootId, sizeof(bootId), "%08lx%08lx%08lx%08lx",
                  static_cast<unsigned long>(esp_random()), static_cast<unsigned long>(esp_random()),
                  static_cast<unsigned long>(esp_random()), static_cast<unsigned long>(esp_random()));

    pinMode(Board::EncoderA, INPUT_PULLUP);
    pinMode(Board::EncoderB, INPUT_PULLUP);
    encoder.reset(encoderPins());
    attachInterrupt(digitalPinToInterrupt(Board::EncoderA), encoderInterrupt, CHANGE);
    attachInterrupt(digitalPinToInterrupt(Board::EncoderB), encoderInterrupt, CHANGE);
    esp_timer_create_args_t timerArgs{};
    timerArgs.callback = sampleEncoder;
    timerArgs.dispatch_method = ESP_TIMER_TASK;
    timerArgs.name = "dial_direction";
    timerArgs.skip_unhandled_events = true;
    if (esp_timer_create(&timerArgs, &encoderTimer) == ESP_OK)
        encoderReady = esp_timer_start_periodic(encoderTimer, 3000) == ESP_OK;

    pinMode(Board::LcdBacklight, OUTPUT);
    digitalWrite(Board::LcdBacklight, LOW);
    displayReady = canvas.begin(Board::LcdFrequency);
    if (displayReady) {
        prepareArcGaps();
        previousFramebuffer = static_cast<uint16_t*>(ps_malloc(Board::Width * Board::Height * sizeof(uint16_t)));
        panel.invertDisplay(false); // ips=true: physical INVON, matching GUITION.
        canvas.fillScreen(Background);
        flushDisplay();
        digitalWrite(Board::LcdBacklight, HIGH);
    }

    pinMode(Board::TouchInterrupt, INPUT_PULLUP);
    pinMode(Board::TouchReset, OUTPUT);
    digitalWrite(Board::TouchReset, LOW);
    delay(200);
    digitalWrite(Board::TouchReset, HIGH);
    delay(200);
    Wire.begin(Board::TouchSda, Board::TouchScl, 400000);
    Wire.setTimeOut(5);
    discardInput();
    lastHello = millis() - 1000;
}

void loop() {
    const uint32_t now = millis();
    if (loopSeen && now - lastLoopAt > maxLoopGapMs) maxLoopGapMs = now - lastLoopAt;
    lastLoopAt = now;
    loopSeen = true;
    const bool connected = static_cast<bool>(Serial);
    if (!connected && wasConnected) {
        ++usbFalseCount;
        session.discardQueuedDesired();
    }
    // HWCDC's SOF-based status can briefly be false while valid host state is
    // still arriving. It gates physical input, not the protocol's 3-second lease.
    // Cancel on both edges so input collected while unavailable is never replayed.
    if (connected != wasConnected) discardInput();
    wasConnected = connected;
    if (session.expire(now)) {
        ++stateExpireCount;
        receiver.clear();
        discardInput();
        needsRender = true;
    }
    // RX data can restore HWCDC status. Drain it even during a false status sample.
    receiveFrames(now);
    if (connected && !session.welcomed() && now - lastHello >= 1000) hello(now);
    handleInput(now, connected);
    const uint32_t renderStartedAt = millis();
    render(now);
    const uint32_t renderElapsed = millis() - renderStartedAt;
    if (renderElapsed > maxRenderMs) maxRenderMs = renderElapsed;
    delay(1);
}
