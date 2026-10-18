#pragma once

#include <ArduinoJson.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

#ifndef DIAL_ISR_ATTR
#define DIAL_ISR_ATTR
#endif

namespace Dial {
constexpr uint32_t StateLifetimeMs = 3000;
constexpr size_t MaxFrameBytes = 4096;

// Retains no partial input after an overlong frame or an embedded NUL.
class LineBuffer {
public:
    bool push(char c) {
        if (c == '\n') {
            const bool ready = !discard_ && size_ > 0;
            bytes_[size_] = '\0';
            size_ = 0;
            discard_ = false;
            return ready;
        }
        if (discard_) return false;
        if (c == '\0' || size_ == MaxFrameBytes - 1) {
            size_ = 0;
            discard_ = true;
            return false;
        }
        bytes_[size_++] = c;
        return false;
    }
    const char* line() const { return bytes_; }
    void clear() { size_ = 0; discard_ = false; bytes_[0] = '\0'; }
private:
    char bytes_[MaxFrameBytes + 1]{};
    size_t size_ = 0;
    bool discard_ = false;
};

// GUITION's SSCM110100 switch has separate active-low direction contacts:
// A (bit 1) is right/+1 and B (bit 0) is left/-1, not quadrature phases.
// Sample every 3 ms, as in the manufacturer's bidi_switch_knob.c. Two low
// samples followed by release qualify a pulse. Overlap and reset while held
// block both directions until the next idle sample, preventing residual input.
class DirectionPulseDecoder {
public:
    void DIAL_ISR_ATTR setEnabled(bool enabled, uint8_t pins) {
        if (enabled_ != enabled) reset(pins);
        enabled_ = enabled;
    }
    bool enabled() const { return enabled_; }
    void DIAL_ISR_ATTR reset(uint8_t pins) {
        active_ = 0; lowSamples_ = 0; blocked_ = (pins & 3) != 3;
    }
    int8_t DIAL_ISR_ATTR update(uint8_t pins) {
        if (!enabled_) { reset(pins); return 0; }
        pins &= 3;
        if (pins == 3) {
            const int8_t step = !blocked_ && lowSamples_ >= 2 ? (active_ == 2 ? 1 : -1) : 0;
            active_ = 0; lowSamples_ = 0; blocked_ = false;
            return step;
        }
        const uint8_t low = (~pins) & 3;
        if (low == 3 || (active_ && active_ != low)) {
            active_ = 0; lowSamples_ = 0; blocked_ = true;
        }
        if (blocked_) return 0;
        active_ = low;
        if (lowSamples_ < 2) ++lowSamples_;
        return 0;
    }
private:
    uint8_t active_ = 0, lowSamples_ = 0;
    bool blocked_ = true, enabled_ = false;
};

// One press on release, restricted to the center. Long holds, swipes, multiple
// fingers, missing samples, and gestures begun before a session are cancelled.
class TapRecognizer {
public:
    void cancel() { active_ = false; blocked_ = true; }
    bool update(bool down, int x, int y, uint32_t now, bool validSample = true) {
        if (!validSample) { cancel(); return false; }
        if (!down) {
            const bool fire = active_ && !blocked_ && now - started_ >= 35 &&
                              now - started_ <= 550;
            active_ = false;
            blocked_ = false;
            return fire;
        }
        const int cx = x - 180, cy = y - 180;
        if (cx * cx + cy * cy > 108 * 108) { cancel(); return false; }
        if (blocked_) return false;
        if (!active_) { active_ = true; started_ = now; x_ = x; y_ = y; }
        const int dx = x - x_, dy = y - y_;
        if (dx * dx + dy * dy > 18 * 18 || now - started_ > 550) cancel();
        return false;
    }
private:
    uint32_t started_ = 0;
    int x_ = 0, y_ = 0;
    bool active_ = false, blocked_ = true;
};

inline bool validUtf8(const char* text, size_t length) {
    for (size_t i = 0; i < length;) {
        const uint8_t a = static_cast<uint8_t>(text[i++]);
        if (a == 0) return false;
        if (a < 0x80) continue;
        unsigned count;
        uint32_t value;
        if (a >= 0xc2 && a <= 0xdf) { count = 1; value = a & 0x1f; }
        else if (a >= 0xe0 && a <= 0xef) { count = 2; value = a & 0x0f; }
        else if (a >= 0xf0 && a <= 0xf4) { count = 3; value = a & 7; }
        else return false;
        if (length - i < count) return false;
        const unsigned total = count;
        while (count--) {
            const uint8_t b = static_cast<uint8_t>(text[i++]);
            if ((b & 0xc0) != 0x80) return false;
            value = (value << 6) | (b & 0x3f);
        }
        if ((total == 1 && value < 0x80) || (total == 2 && value < 0x800) ||
            (total == 3 && value < 0x10000) || value > 0x10ffff ||
            (value >= 0xd800 && value <= 0xdfff)) return false;
    }
    return true;
}

template <size_t N>
inline bool readString(JsonVariantConst source, char (&dest)[N], bool allowEmpty = false) {
    if (!source.is<const char*>()) return false;
    JsonString value = source.as<JsonString>();
    const size_t size = value.size();
    if (size >= N || (!allowEmpty && size == 0) || !validUtf8(value.c_str(), size)) return false;
    // Protocol strings are single-line display text or identifiers.
    for (size_t i = 0; i < size; ++i)
        if (static_cast<unsigned char>(value.c_str()[i]) < 0x20) return false;
    std::memcpy(dest, value.c_str(), size);
    dest[size] = '\0';
    return true;
}

enum class Kind { Cooling, Lighting };
enum class AckStatus { None, Pending, Confirmed, Saved, Unverified, Rejected };
struct State {
    char sessionId[65]{};
    char stateId[65]{};
    char targetId[193]{};
    char label[193]{};
    char message[257]{};
    char operationId[65]{};
    Kind kind = Kind::Cooling;
    AckStatus ackStatus = AckStatus::None;
    uint32_t ackSeq = 0;
    int32_t value = 0, min = 0, max = 100, step = 1, index = 0, count = 0, memberCount = 1;
    int32_t rpmMin = 0, rpmMax = 0, rpmCount = 0;
    int32_t cpuTempDeciC = 0, gpuTempDeciC = 0, waterTempDeciC = 0;
    bool enabled = false, busy = false, valueMixed = false;
    bool rpmProvided = false;
    bool temperaturesProvided = false, cpuTempValid = false, gpuTempValid = false, waterTempValid = false;
};

inline bool readInt(JsonVariantConst input, int32_t& output) {
    if (!input.is<int32_t>() || input.is<bool>()) return false;
    output = input.as<int32_t>();
    return true;
}

inline bool readTemperature(JsonVariantConst input, int32_t& output, bool& valid) {
    // Missing telemetry is distinct from an observed zero. All three optional
    // temperatures are replaced on every frame, never carried across samples.
    valid = !input.isNull();
    return !valid || (readInt(input, output) && output >= 0 && output <= 1500);
}

inline bool parseState(JsonObjectConst object, State& output, bool responsive = false) {
    State value;
    char kind[10];
    if (!readString(object["sessionId"], value.sessionId) ||
        !readString(object["stateId"], value.stateId) ||
        !readString(object["targetId"], value.targetId, true) ||
        !readString(object["label"], value.label, true) ||
        !readString(object["message"], value.message, true) ||
        !readString(object["kind"], kind) ||
        !readInt(object["value"], value.value) || !readInt(object["min"], value.min) ||
        !readInt(object["max"], value.max) || !readInt(object["step"], value.step) ||
        !readInt(object["index"], value.index) || !readInt(object["count"], value.count) ||
        !object["enabled"].is<bool>() || !object["busy"].is<bool>()) return false;
    if (!std::strcmp(kind, "cooling")) value.kind = Kind::Cooling;
    else if (!std::strcmp(kind, "lighting")) value.kind = Kind::Lighting;
    else return false;
    value.enabled = object["enabled"].as<bool>();
    value.busy = object["busy"].as<bool>();
    // value is the confirmed observation, which may be below a cooling device's
    // minimum permitted fixed setting (for example, a currently stopped fan).
    if (value.min < 0 || value.max > 100 || value.min > value.max ||
        value.value < 0 || value.value > 100 || value.step < 1 ||
        value.step > 100 || value.count < 0 || value.count > 10000) return false;
    if (value.count == 0) {
        if (value.index != 0 || value.targetId[0] || value.enabled) return false;
    } else if (value.index < 1 || value.index > value.count || !value.targetId[0]) return false;
    // Optional group metadata preserves the single-target contract for older PCs.
    value.memberCount = value.count ? 1 : 0;
    if (!object["memberCount"].isUnbound() && !readInt(object["memberCount"], value.memberCount)) return false;
    if (!object["valueMixed"].isUnbound()) {
        if (!object["valueMixed"].is<bool>()) return false;
        value.valueMixed = object["valueMixed"].as<bool>();
    }
    if (value.memberCount < (value.count ? 1 : 0) || value.memberCount > (value.count ? 64 : 0) ||
        (value.valueMixed && value.memberCount < 2)) return false;
    // RPM is optional observational telemetry, never an input authority or
    // confirmation. An incomplete group must not look like a complete range.
    value.rpmProvided = !object["rpmMin"].isUnbound() || !object["rpmMax"].isUnbound() || !object["rpmCount"].isUnbound();
    if (value.rpmProvided) {
        if (!readInt(object["rpmCount"], value.rpmCount) || value.rpmCount < 0 || value.rpmCount > value.memberCount) return false;
        if (value.rpmCount == 0) {
            if (!object["rpmMin"].isNull() || !object["rpmMax"].isNull()) return false;
        } else {
            if (!readInt(object["rpmMin"], value.rpmMin) || !readInt(object["rpmMax"], value.rpmMax) ||
                value.rpmMin < 0 || value.rpmMax > 1000000 || value.rpmMin > value.rpmMax ||
                (value.rpmCount == 1 && value.rpmMin != value.rpmMax)) return false;
        }
    }
    value.temperaturesProvided = !object["cpuTempDeciC"].isUnbound() ||
        !object["gpuTempDeciC"].isUnbound() || !object["waterTempDeciC"].isUnbound();
    if (!readTemperature(object["cpuTempDeciC"], value.cpuTempDeciC, value.cpuTempValid) ||
        !readTemperature(object["gpuTempDeciC"], value.gpuTempDeciC, value.gpuTempValid) ||
        !readTemperature(object["waterTempDeciC"], value.waterTempDeciC, value.waterTempValid)) return false;
    if (responsive) {
        char status[12];
        if (!readString(object["operationId"], value.operationId) ||
            !readString(object["ackStatus"], status) ||
            !object["ackSeq"].is<uint32_t>() || object["ackSeq"].is<bool>()) return false;
        value.ackSeq = object["ackSeq"].as<uint32_t>();
        if (!std::strcmp(status, "none")) value.ackStatus = AckStatus::None;
        else if (!std::strcmp(status, "pending")) value.ackStatus = AckStatus::Pending;
        else if (!std::strcmp(status, "confirmed")) value.ackStatus = AckStatus::Confirmed;
        else if (!std::strcmp(status, "saved")) value.ackStatus = AckStatus::Saved;
        else if (!std::strcmp(status, "unverified")) value.ackStatus = AckStatus::Unverified;
        else if (!std::strcmp(status, "rejected")) value.ackStatus = AckStatus::Rejected;
        else return false;
        if ((value.ackStatus == AckStatus::None) != (value.ackSeq == 0)) return false;
    }
    output = value;
    return true;
}

// ArduinoJson stops when the root closes. Tracking the read cursor also rejects
// appended JSON or garbage, which must not be interpreted as a valid frame.
class JsonReader {
public:
    explicit JsonReader(const char* input) : input_(input), size_(std::strlen(input)) {}
    int read() { return index_ < size_ ? static_cast<unsigned char>(input_[index_++]) : -1; }
    size_t readBytes(char* buffer, size_t length) {
        size_t count = size_ - index_;
        if (count > length) count = length;
        std::memcpy(buffer, input_ + index_, count); index_ += count; return count;
    }
    bool onlyWhitespaceRemains() const {
        for (size_t i = index_; i < size_; ++i)
            if (input_[i] != ' ' && input_[i] != '\t' && input_[i] != '\r') return false;
        return true;
    }
private:
    const char* input_;
    size_t size_, index_ = 0;
};

enum class FrameResult { Rejected, Identify, Welcome, State };
class Session {
public:
    FrameResult receive(const char* line, uint32_t now) {
        if (!line || std::strlen(line) >= MaxFrameBytes || !validUtf8(line, std::strlen(line))) return FrameResult::Rejected;
        JsonDocument document;
        JsonReader reader(line);
        if (deserializeJson(document, reader, DeserializationOption::NestingLimit(3)) ||
            !reader.onlyWhitespaceRemains() || !document.is<JsonObject>()) return FrameResult::Rejected;
        const JsonObjectConst object = document.as<JsonObjectConst>();
        if (!object["v"].is<int>() || object["v"].as<int>() != 1) return FrameResult::Rejected;
        char type[17];
        if (!readString(object["type"], type)) return FrameResult::Rejected;
        if (!std::strcmp(type, "identify")) return FrameResult::Identify;
        if (!std::strcmp(type, "welcome")) {
            char id[65];
            if (!readString(object["sessionId"], id)) return FrameResult::Rejected;
            if (!object["responsiveInput"].isNull() &&
                (!object["responsiveInput"].is<int>() || object["responsiveInput"].is<bool>())) return FrameResult::Rejected;
            const bool responsive = object["responsiveInput"].as<int>() == 1;
            if (std::strcmp(id, sessionId_) || responsive != responsive_) {
                state_ = State{}; hasState_ = false; awaitingState_ = false; cancelIntent();
            }
            responsive_ = responsive;
            std::memcpy(sessionId_, id, std::strlen(id) + 1);
            welcomeAt_ = now;
            return FrameResult::Welcome;
        }
        if (!std::strcmp(type, "state")) {
            State next;
            if (!sessionId_[0] || !parseState(object, next, responsive_) || std::strcmp(sessionId_, next.sessionId)) return FrameResult::Rejected;
            if (responsive_) reconcileIntent(next, now);
            state_ = next;
            receivedAt_ = now;
            hasState_ = true;
            awaitingState_ = false;
            return FrameResult::State;
        }
        return FrameResult::Rejected;
    }
    bool expire(uint32_t now) {
        if (sessionId_[0] && (now - (hasState_ ? receivedAt_ : welcomeAt_) >= StateLifetimeMs ||
            (responsive_ && inFlight_ && now - ackAt_ >= StateLifetimeMs))) {
            disconnect(); return true;
        }
        return false;
    }
    void disconnect() {
        sessionId_[0] = '\0'; hasState_ = false; awaitingState_ = false;
        responsive_ = false; state_ = State{}; cancelIntent();
    }
    bool welcomed() const { return sessionId_[0] != '\0'; }
    bool hasState() const { return hasState_; }
    bool fresh(uint32_t now) const { return hasState_ && now - receivedAt_ < StateLifetimeMs; }
    bool responsive() const { return responsive_; }
    bool hasPreview() const { return responsive_ && preview_; }
    int32_t desiredValue() const { return hasPreview() ? desired_ : state_.value; }
    bool inFlight() const { return inFlight_; }
    bool hasQueuedDesired() const { return preview_ && (!inFlight_ || desired_ != sentValue_); }
    uint32_t inFlightSeq() const { return inFlight_ ? sentSeq_ : 0; }
    uint32_t cancellationSerial() const { return cancellationSerial_; }
    bool canAdjust(uint32_t now) const {
        if (!responsive_ || !fresh(now) || state_.count == 0 || awaitingState_ ||
            sequence_ == std::numeric_limits<uint32_t>::max()) return false;
        // Only our own, continuous operation can accept rotation while busy.
        return inFlight_ ? inFlightSet_ : state_.enabled && !state_.busy;
    }
    bool adjustDesired(int delta, uint32_t now) {
        if (!canAdjust(now) || delta == 0 || delta < -20 || delta > 20) return false;
        const int32_t previous = desiredValue();
        if (previous < state_.min && delta < 0) return false;
        int64_t next = static_cast<int64_t>(previous) + static_cast<int64_t>(delta) * state_.step;
        if (next < state_.min) next = state_.min;
        if (next > state_.max) next = state_.max;
        // A mixed group at its upper bound still needs a set at that same value
        // to bring lower members up to the common value.
        if (next == previous && (!state_.valueMixed || preview_)) return false;
        desired_ = static_cast<int32_t>(next);
        preview_ = inFlight_ || state_.valueMixed || desired_ != state_.value;
        return true;
    }
    bool canSendDesired(uint32_t now) const {
        return canAdjust(now) && !inFlight_ && preview_ && (state_.valueMixed || desired_ != state_.value);
    }
    bool makeSet(uint32_t now, JsonDocument& output) {
        if (!canSendDesired(now)) return false;
        makeBase(output);
        output["action"] = "set";
        output["value"] = desired_;
        inFlight_ = inFlightSet_ = true;
        sentSeq_ = sequence_;
        sentValue_ = desired_;
        ackAt_ = now;
        return true;
    }
    // A failed write is not retried. A request whose delivery is uncertain must
    // remain single-flight until its explicit result or the state lease expires.
    void writeFailed() { discardQueuedDesired(); }
    void discardQueuedDesired() {
        if (inFlight_ && inFlightSet_) { desired_ = sentValue_; preview_ = true; }
        else { preview_ = false; desired_ = state_.value; }
    }
    bool canInput(uint32_t now, bool press = false) const {
        if (responsive_) return press && fresh(now) && !state_.busy && !inFlight_ && !preview_ &&
            !awaitingState_ && state_.count > 0 && sequence_ < std::numeric_limits<uint32_t>::max();
        return fresh(now) && (press || state_.enabled) && !state_.busy && !awaitingState_ &&
               state_.count > 0 && sequence_ < std::numeric_limits<uint32_t>::max();
    }
    const State& state() const { return state_; }
    // Prepare only immediately before a transport write. The caller must not
    // retain or retry this frame after a later state/session transition.
    bool makeInput(bool press, int delta, uint32_t now, JsonDocument& output) {
        if (!canInput(now, press) || (press ? delta != 0 : (delta == 0 || delta < -20 || delta > 20))) return false;
        makeBase(output);
        output["action"] = press ? "press" : "rotate";
        output["delta"] = delta;
        if (responsive_) { inFlight_ = true; inFlightSet_ = false; sentSeq_ = sequence_; ackAt_ = now; }
        else awaitingState_ = true;
        return true;
    }
private:
    void makeBase(JsonDocument& output) {
        output.clear();
        output["v"] = 1;
        output["type"] = "input";
        output["sessionId"] = sessionId_;
        output["stateId"] = state_.stateId;
        output["targetId"] = state_.targetId;
        output["seq"] = ++sequence_;
    }
    void cancelIntent() {
        preview_ = inFlight_ = inFlightSet_ = false; desired_ = sentValue_ = 0; sentSeq_ = 0;
        ++cancellationSerial_;
    }
    void reconcileIntent(const State& next, uint32_t now) {
        const bool sameContext = hasState_ && !std::strcmp(state_.operationId, next.operationId) &&
            !std::strcmp(state_.targetId, next.targetId) && state_.kind == next.kind &&
            state_.min == next.min && state_.max == next.max && state_.step == next.step &&
            state_.memberCount == next.memberCount && next.count > 0;
        if (!sameContext) { cancelIntent(); desired_ = next.value; return; }
        if (inFlight_) {
            if (next.ackSeq == sentSeq_) ackAt_ = now;
            if (next.ackSeq > sentSeq_ || (next.ackSeq == sentSeq_ &&
                (next.ackStatus == AckStatus::Unverified || next.ackStatus == AckStatus::Rejected ||
                 (next.ackStatus == AckStatus::Saved && next.kind != Kind::Lighting) ||
                 ((next.ackStatus == AckStatus::Confirmed || next.ackStatus == AckStatus::Saved) && next.valueMixed)))) {
                cancelIntent(); desired_ = next.value; return;
            }
            if (next.ackSeq == sentSeq_ && (next.ackStatus == AckStatus::Confirmed || next.ackStatus == AckStatus::Saved)) {
                const bool laterRotation = inFlightSet_ && desired_ != sentValue_;
                inFlight_ = inFlightSet_ = false;
                preview_ = laterRotation && desired_ != next.value && next.enabled && !next.busy;
                if (!preview_) desired_ = next.value;
            }
            // Ordinary heartbeat and stale result never release single-flight.
        } else if (!next.enabled || next.busy || (!next.valueMixed && desired_ == next.value)) {
            preview_ = false; desired_ = next.value;
        }
        if (!preview_) desired_ = next.value;
    }
    char sessionId_[65]{};
    State state_{};
    uint32_t receivedAt_ = 0, welcomeAt_ = 0, sequence_ = 0;
    bool hasState_ = false, awaitingState_ = false;
    bool responsive_ = false, preview_ = false, inFlight_ = false, inFlightSet_ = false;
    int32_t desired_ = 0, sentValue_ = 0;
    uint32_t sentSeq_ = 0;
    uint32_t ackAt_ = 0;
    uint32_t cancellationSerial_ = 0;
};
} // namespace Dial
