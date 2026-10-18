#include <cstdlib>
#include <iostream>
#include <string>
#include <vector>
#include "../src/dial_core.h"
#include "../src/display_diff.h"

static unsigned checks = 0;
#define CHECK(condition) do { ++checks; if (!(condition)) { std::cerr << "FAIL line " << __LINE__ << ": " << #condition << '\n'; std::exit(1); } } while (false)

using Dial::FrameResult;
static const char* Welcome = R"({"v":1,"type":"welcome","sessionId":"session-1"})";
static const char* StateFrame = R"({"v":1,"type":"state","sessionId":"session-1","stateId":"state-1","targetId":"fan-1","label":"Fan","kind":"cooling","value":45,"min":20,"max":100,"step":1,"index":1,"count":3,"enabled":true,"busy":false,"message":"Confirmed"})";

static std::string encode(const JsonDocument& document) {
    std::string output;
    serializeJson(document, output);
    return output;
}
static JsonDocument stateDocument() {
    JsonDocument document;
    CHECK(!deserializeJson(document, StateFrame));
    return document;
}
static void connect(Dial::Session& session, uint32_t now = 100) {
    CHECK(session.receive(Welcome, now) == FrameResult::Welcome);
    CHECK(session.receive(StateFrame, now) == FrameResult::State);
}

static void framingTests() {
    Dial::LineBuffer framer;
    CHECK(!framer.push('\n'));
    std::string maximum(Dial::MaxFrameBytes - 1, 'x');
    for (char c : maximum) CHECK(!framer.push(c));
    CHECK(framer.push('\n'));
    CHECK(std::string(framer.line()) == maximum);
    for (size_t i = 0; i < Dial::MaxFrameBytes + 50; ++i) CHECK(!framer.push('x'));
    CHECK(!framer.push('\n'));
    CHECK(!framer.push('a'));
    CHECK(framer.push('\n'));
    CHECK(std::string(framer.line()) == "a");
    CHECK(!framer.push('a')); CHECK(!framer.push('\0')); CHECK(!framer.push('b'));
    CHECK(!framer.push('\n'));
    CHECK(!framer.push('{')); framer.clear();
    CHECK(!framer.push('}')); CHECK(!framer.push('\r')); CHECK(framer.push('\n'));
    CHECK(std::string(framer.line()) == "}\r");
}

static void encoderTests() {
    Dial::DirectionPulseDecoder decoder;
    decoder.reset(3);
    decoder.setEnabled(true, 3);
    CHECK(decoder.update(3) == 0);
    CHECK(decoder.update(1) == 0); CHECK(decoder.update(1) == 0);
    CHECK(decoder.update(3) == 1); CHECK(decoder.update(3) == 0); // A/right
    CHECK(decoder.update(2) == 0); CHECK(decoder.update(2) == 0);
    CHECK(decoder.update(3) == -1); CHECK(decoder.update(3) == 0); // B/left
    // One low sample is bounce, not a qualified direction pulse.
    for (unsigned i = 0; i < 100; ++i) {
        CHECK(decoder.update(1) == 0); CHECK(decoder.update(3) == 0);
        CHECK(decoder.update(2) == 0); CHECK(decoder.update(3) == 0);
    }
    // Long holds never repeat or overflow the low-sample counter.
    for (unsigned i = 0; i < 1000; ++i) CHECK(decoder.update(1) == 0);
    CHECK(decoder.update(3) == 1); CHECK(decoder.update(3) == 0);
    for (unsigned i = 0; i < 1000; ++i) CHECK(decoder.update(2) == 0);
    CHECK(decoder.update(3) == -1); CHECK(decoder.update(3) == 0);
    // Simultaneous/overlapping contacts are ambiguous; wait for full release.
    CHECK(decoder.update(0) == 0); CHECK(decoder.update(0) == 0);
    CHECK(decoder.update(1) == 0); CHECK(decoder.update(1) == 0); CHECK(decoder.update(3) == 0);
    CHECK(decoder.update(1) == 0); CHECK(decoder.update(1) == 0);
    CHECK(decoder.update(2) == 0); CHECK(decoder.update(2) == 0); CHECK(decoder.update(3) == 0);
    CHECK(decoder.update(2) == 0); CHECK(decoder.update(2) == 0);
    CHECK(decoder.update(0) == 0); CHECK(decoder.update(2) == 0); CHECK(decoder.update(3) == 0);
    // Resets at busy/disabled/stale/transport/target changes use current pins.
    // A held contact and its release cannot become input after resumption.
    for (const uint8_t held : {uint8_t(0), uint8_t(1), uint8_t(2)}) {
        decoder.reset(3);
        CHECK(decoder.update(held) == 0); CHECK(decoder.update(held) == 0);
        decoder.reset(held);
        for (unsigned i = 0; i < 20; ++i) CHECK(decoder.update(held) == 0);
        CHECK(decoder.update(3) == 0);
        CHECK(decoder.update(1) == 0); CHECK(decoder.update(1) == 0); CHECK(decoder.update(3) == 1);
    }
    // A same-state heartbeat may enable input after an ACK. Nothing sampled
    // while disabled/awaiting may survive that transition, even a held contact.
    for (const uint8_t held : {uint8_t(1), uint8_t(2)}) {
        decoder.setEnabled(false, 3);
        CHECK(!decoder.enabled());
        CHECK(decoder.update(held) == 0); CHECK(decoder.update(held) == 0);
        CHECK(decoder.update(3) == 0);
        CHECK(decoder.update(held) == 0); CHECK(decoder.update(held) == 0);
        decoder.setEnabled(true, held);
        CHECK(decoder.enabled()); CHECK(decoder.update(held) == 0);
        CHECK(decoder.update(3) == 0);
        CHECK(decoder.update(held) == 0); CHECK(decoder.update(held) == 0);
        decoder.setEnabled(false, held); // disable after a fully qualified low
        decoder.setEnabled(true, held);
        CHECK(decoder.update(3) == 0);
        CHECK(decoder.update(held) == 0); CHECK(decoder.update(held) == 0);
        CHECK(decoder.update(3) == (held == 1 ? 1 : -1));
    }
}

static void touchTests() {
    Dial::TapRecognizer tap;
    CHECK(!tap.update(true, 180, 180, 0)); // initial contact cannot be a press
    CHECK(!tap.update(false, 0, 0, 50));
    CHECK(!tap.update(true, 180, 180, 100));
    CHECK(tap.update(false, 0, 0, 135));
    CHECK(!tap.update(false, 0, 0, 140)); // exactly one release
    CHECK(!tap.update(true, 180, 180, 200)); CHECK(!tap.update(false, 0, 0, 234));
    CHECK(!tap.update(true, 180, 180, 300)); CHECK(tap.update(false, 0, 0, 850));
    CHECK(!tap.update(true, 180, 180, 900)); CHECK(!tap.update(false, 0, 0, 1451));
    CHECK(!tap.update(true, 180, 180, 1500)); CHECK(!tap.update(true, 199, 180, 1550));
    CHECK(!tap.update(true, 180, 180, 1560)); CHECK(!tap.update(false, 0, 0, 1600));
    CHECK(!tap.update(true, 300, 180, 1700)); CHECK(!tap.update(false, 0, 0, 1800));
    CHECK(!tap.update(true, 180, 180, 1900)); CHECK(!tap.update(true, 180, 180, 1950, false));
    CHECK(!tap.update(false, 0, 0, 2000));
    CHECK(!tap.update(true, 180, 180, 2100)); tap.cancel();
    CHECK(!tap.update(false, 0, 0, 2200));
    CHECK(!tap.update(true, 180, 180, UINT32_MAX - 30)); CHECK(tap.update(false, 0, 0, 20));
}

static void schemaTests() {
    Dial::Session session;
    CHECK(session.receive(StateFrame, 0) == FrameResult::Rejected);
    CHECK(session.receive(R"({"v":1,"type":"identify"})", 0) == FrameResult::Identify);
    CHECK(!session.welcomed());
    CHECK(session.receive(Welcome, 1) == FrameResult::Welcome);
    CHECK(!session.canInput(1));
    for (int bad = 0; bad < 31; ++bad) {
        JsonDocument document = stateDocument();
        switch (bad) {
        case 0: document["v"] = 2; break;
        case 1: document["sessionId"] = "other"; break;
        case 2: document["stateId"] = ""; break;
        case 3: document["targetId"] = ""; break;
        case 4: document["label"] = std::string(193, 'a'); break;
        case 5: document["message"] = std::string(257, 'a'); break;
        case 6: document["kind"] = "fan"; break;
        case 7: document["value"] = "30"; break;
        case 8: document["value"] = true; break;
        case 9: document["value"] = 30.5; break;
        case 10: document["value"] = -1; break;
        case 11: document["value"] = 101; break;
        case 12: document["min"] = -1; break;
        case 13: document["max"] = 101; break;
        case 14: document["min"] = 50; document["max"] = 40; break;
        case 15: document["step"] = 0; break;
        case 16: document["step"] = 101; break;
        case 17: document["index"] = 0; break;
        case 18: document["index"] = 4; break;
        case 19: document["count"] = -1; break;
        case 20: document["count"] = 10001; break;
        case 21: document["enabled"] = 1; break;
        case 22: document["busy"] = "false"; break;
        case 23: document.remove("message"); break;
        case 24: document["targetId"] = std::string(193, 'a'); break;
        case 25: document["stateId"] = std::string(65, 'a'); break;
        case 26: document["sessionId"] = std::string(65, 'a'); break;
        case 27: document["label"] = "line\nbreak"; break;
        case 28: document["step"] = UINT64_MAX; break;
        case 29: document["value"] = nullptr; break;
        case 30: document["count"] = 0; break;
        }
        CHECK(session.receive(encode(document).c_str(), 2) == FrameResult::Rejected);
        CHECK(!session.hasState());
    }
    CHECK(session.receive((std::string(StateFrame) + " garbage").c_str(), 2) == FrameResult::Rejected);
    CHECK(session.receive((std::string(StateFrame) + "{}").c_str(), 2) == FrameResult::Rejected);
    CHECK(session.receive("[]", 2) == FrameResult::Rejected);
    CHECK(session.receive(R"({"v":1,"type":"welcome","sessionId":"a\u0000b"})", 2) == FrameResult::Rejected);
    CHECK(session.receive(R"({"v":1,"type":"identify\u0000x"})", 2) == FrameResult::Rejected);
    CHECK(session.receive(std::string(4096, ' ').c_str(), 2) == FrameResult::Rejected);
    CHECK(session.receive((std::string(StateFrame) + " \t\r").c_str(), 3) == FrameResult::State);
    JsonDocument boundary = stateDocument();
    boundary["stateId"] = std::string(64, 's');
    boundary["targetId"] = std::string(192, 't');
    boundary["label"] = std::string(192, 'l');
    boundary["message"] = std::string(256, 'm');
    boundary["value"] = 0; // observed stopped fan below the fixed-output minimum
    CHECK(session.receive(encode(boundary).c_str(), 4) == FrameResult::State);
    boundary["value"] = 100; boundary["max"] = 80;
    CHECK(session.receive(encode(boundary).c_str(), 5) == FrameResult::State);
    std::string japanese;
    for (int i = 0; i < 64; ++i) japanese += "\xe3\x81\x82";
    boundary["label"] = japanese;
    CHECK(session.receive(encode(boundary).c_str(), 6) == FrameResult::State);
    boundary["label"] = japanese + "a";
    CHECK(session.receive(encode(boundary).c_str(), 7) == FrameResult::Rejected);
    CHECK(Dial::validUtf8("\xe3\x81\x82", 3));
    CHECK(!Dial::validUtf8("\xc0\xaf", 2));
    CHECK(!Dial::validUtf8("\xed\xa0\x80", 3));
    CHECK(!Dial::validUtf8("\xf4\x90\x80\x80", 4));
    CHECK(!Dial::validUtf8("\xe3\x81", 2));
    CHECK(!Dial::validUtf8("a\0b", 3));
}

static void sessionTests() {
    Dial::Session session;
    connect(session);
    CHECK(session.fresh(3099)); CHECK(!session.fresh(3100));
    CHECK(session.canInput(100)); CHECK(session.canInput(100, true));
    JsonDocument input;
    CHECK(!session.makeInput(false, 0, 100, input));
    CHECK(!session.makeInput(false, 21, 100, input));
    CHECK(!session.makeInput(false, -21, 100, input));
    CHECK(!session.makeInput(true, 1, 100, input));
    CHECK(session.makeInput(false, 20, 100, input));
    CHECK(input["seq"].as<uint32_t>() == 1);
    CHECK(std::string(input["sessionId"].as<const char*>()) == "session-1");
    CHECK(std::string(input["stateId"].as<const char*>()) == "state-1");
    CHECK(std::string(input["targetId"].as<const char*>()) == "fan-1");
    CHECK(!session.canInput(101));
    CHECK(!session.makeInput(true, 0, 101, input));
    CHECK(session.receive(StateFrame, 110) == FrameResult::State);
    CHECK(session.makeInput(true, 0, 110, input));
    CHECK(input["seq"].as<uint32_t>() == 2);
    CHECK(std::string(input["action"].as<const char*>()) == "press");
    CHECK(input["delta"].as<int>() == 0);
    JsonDocument state = stateDocument();
    state["enabled"] = false;
    CHECK(session.receive(encode(state).c_str(), 120) == FrameResult::State);
    CHECK(!session.canInput(120)); CHECK(session.canInput(120, true));
    CHECK(session.makeInput(true, 0, 120, input));
    state["busy"] = true;
    CHECK(session.receive(encode(state).c_str(), 130) == FrameResult::State);
    CHECK(!session.canInput(130)); CHECK(!session.canInput(130, true));
    CHECK(!session.expire(3129)); CHECK(session.expire(3130));
    CHECK(!session.welcomed()); CHECK(!session.hasState());
    CHECK(session.receive(StateFrame, 3140) == FrameResult::Rejected);
    connect(session, 3200);
    CHECK(session.makeInput(false, -20, 3200, input));
    CHECK(input["seq"].as<uint32_t>() == 4); // never reset sequence on reconnect
    CHECK(session.receive(R"({"v":1,"type":"welcome","sessionId":"new"})", 3300) == FrameResult::Welcome);
    CHECK(!session.canInput(3300));
    CHECK(session.receive(StateFrame, 3400) == FrameResult::Rejected);
    CHECK(session.expire(6300)); // welcome without state also times out
    connect(session, UINT32_MAX - 100);
    CHECK(session.fresh(100)); CHECK(!session.expire(100)); CHECK(session.expire(2900));
    connect(session, 5000);
    state = stateDocument();
    state["targetId"] = ""; state["index"] = 0; state["count"] = 0;
    state["enabled"] = false; state["value"] = 0;
    CHECK(session.receive(encode(state).c_str(), 5000) == FrameResult::State);
    CHECK(!session.canInput(5000)); CHECK(!session.canInput(5000, true));
    session.disconnect();
    CHECK(!session.welcomed()); CHECK(!session.hasState());
}

static const char* ResponsiveWelcome = R"({"v":1,"type":"welcome","sessionId":"session-1","responsiveInput":1})";
static JsonDocument responsiveState(uint32_t seq = 0, const char* status = "none") {
    JsonDocument state = stateDocument();
    state["operationId"] = "operation-1"; state["ackSeq"] = seq; state["ackStatus"] = status;
    return state;
}
static void connectResponsive(Dial::Session& session, uint32_t now = 100) {
    CHECK(session.receive(ResponsiveWelcome, now) == FrameResult::Welcome);
    CHECK(session.receive(encode(responsiveState()).c_str(), now) == FrameResult::State);
    CHECK(session.responsive());
}
static void responsiveSchemaTests() {
    for (unsigned issue = 0; issue < 12; ++issue) {
        Dial::Session session; CHECK(session.receive(ResponsiveWelcome, 0) == FrameResult::Welcome);
        JsonDocument state = responsiveState();
        switch (issue) {
        case 0: state.remove("ackSeq"); break;
        case 1: state["ackSeq"] = -1; break;
        case 2: state["ackSeq"] = UINT64_MAX; break;
        case 3: state["ackSeq"] = true; break;
        case 4: state.remove("ackStatus"); break;
        case 5: state["ackStatus"] = "success"; break;
        case 6: state["ackStatus"] = "pending"; break;
        case 7: state["ackSeq"] = 1; break;
        case 8: state.remove("operationId"); break;
        case 9: state["operationId"] = ""; break;
        case 10: state["operationId"] = std::string(65, 'o'); break;
        case 11: state["ackSeq"] = 1.5; break;
        }
        CHECK(session.receive(encode(state).c_str(), 1) == FrameResult::Rejected);
        CHECK(!session.hasState());
    }
    Dial::Session session; connectResponsive(session);
    JsonDocument maximum = responsiveState(UINT32_MAX, "confirmed");
    maximum["operationId"] = std::string(64, 'o');
    CHECK(session.receive(encode(maximum).c_str(), 101) == FrameResult::State);
    maximum["ackStatus"] = "saved";
    // A prior lighting ACK may remain cached after the current target changes.
    CHECK(session.receive(encode(maximum).c_str(), 101) == FrameResult::State);
    CHECK(session.receive(R"({"v":1,"type":"welcome","sessionId":"session-1","responsiveInput":true})", 102) == FrameResult::Rejected);
    CHECK(session.receive(Welcome, 103) == FrameResult::Welcome);
    CHECK(!session.responsive()); CHECK(!session.hasState());
    CHECK(session.receive(StateFrame, 104) == FrameResult::State);
    CHECK(session.canInput(104)); CHECK(!session.canAdjust(104));
}
static void responsiveFlowTests() {
    Dial::Session session; connectResponsive(session);
    JsonDocument input;
    CHECK(!session.hasPreview()); CHECK(session.desiredValue() == 45);
    CHECK(!session.makeInput(false, 1, 100, input));
    CHECK(session.adjustDesired(3, 100)); CHECK(session.desiredValue() == 48);
    CHECK(session.state().value == 45); CHECK(session.hasPreview()); CHECK(session.hasQueuedDesired());
    CHECK(session.makeSet(100, input)); CHECK(session.inFlightSeq() == 1);
    CHECK(input["action"].as<std::string>() == "set"); CHECK(input["value"].as<int>() == 48);
    CHECK(input["delta"].isNull()); CHECK(!session.hasQueuedDesired());
    CHECK(!session.makeSet(101, input)); CHECK(!session.canInput(101, true));
    CHECK(session.adjustDesired(7, 101)); CHECK(session.desiredValue() == 55);
    CHECK(session.hasQueuedDesired());
    auto state = responsiveState(); state["stateId"] = "heartbeat-new-id";
    CHECK(session.receive(encode(state).c_str(), 200) == FrameResult::State);
    CHECK(session.inFlight()); CHECK(!session.canSendDesired(200)); CHECK(session.desiredValue() == 55);
    state = responsiveState(1, "pending"); state["busy"] = true; state["enabled"] = false; state["stateId"] = "busy-id";
    CHECK(session.receive(encode(state).c_str(), 300) == FrameResult::State);
    CHECK(session.canAdjust(300)); CHECK(session.adjustDesired(-2, 300)); CHECK(session.desiredValue() == 53);
    CHECK(session.state().value == 45); CHECK(!session.canSendDesired(300));
    state = responsiveState(1, "confirmed"); state["value"] = 48; state["stateId"] = "completed-id";
    CHECK(session.receive(encode(state).c_str(), 400) == FrameResult::State);
    CHECK(!session.inFlight()); CHECK(session.desiredValue() == 53); CHECK(session.canSendDesired(400));
    CHECK(session.makeSet(400, input)); CHECK(input["seq"].as<uint32_t>() == 2);
    CHECK(input["value"].as<int>() == 53); CHECK(input["stateId"].as<std::string>() == "completed-id");
    CHECK(session.receive(encode(state).c_str(), 500) == FrameResult::State); // old completion cannot release seq 2
    CHECK(session.inFlight()); CHECK(!session.canSendDesired(500));
    state = responsiveState(2, "confirmed"); state["value"] = 53;
    CHECK(session.receive(encode(state).c_str(), 600) == FrameResult::State);
    CHECK(!session.inFlight()); CHECK(!session.hasPreview()); CHECK(session.desiredValue() == 53);
    CHECK(!session.canSendDesired(600)); CHECK(session.canInput(600, true));
    CHECK(session.makeInput(true, 0, 600, input)); CHECK(input["seq"].as<uint32_t>() == 3);
    CHECK(!session.canAdjust(600)); CHECK(!session.canInput(600, true));
    state = responsiveState(3, "confirmed"); state["targetId"] = "fan-2"; state["operationId"] = "operation-2";
    CHECK(session.receive(encode(state).c_str(), 700) == FrameResult::State);
    CHECK(!session.inFlight()); CHECK(!session.hasPreview()); CHECK(session.canAdjust(700));
    state = responsiveState(); state["kind"] = "lighting"; state["operationId"] = "lighting-1";
    CHECK(session.receive(encode(state).c_str(), 800) == FrameResult::State);
    CHECK(session.adjustDesired(2, 800)); CHECK(session.makeSet(800, input)); CHECK(session.adjustDesired(3, 801));
    state["ackSeq"] = session.inFlightSeq(); state["ackStatus"] = "saved"; state["value"] = 47;
    CHECK(session.receive(encode(state).c_str(), 900) == FrameResult::State);
    CHECK(session.canSendDesired(900)); CHECK(session.desiredValue() == 50);
    CHECK(session.state().ackStatus == Dial::AckStatus::Saved); // never promoted to physical confirmed
}
static void responsiveCancellationTests() {
    for (unsigned reason = 0; reason < 12; ++reason) {
        Dial::Session session; connectResponsive(session); JsonDocument input;
        CHECK(session.adjustDesired(1, 100)); CHECK(session.makeSet(100, input)); CHECK(session.adjustDesired(10, 101));
        const auto cancellation = session.cancellationSerial();
        auto state = responsiveState(1, "confirmed"); state["value"] = 46;
        switch (reason) {
        case 0: state["ackStatus"] = "rejected"; break;
        case 1: state["ackStatus"] = "unverified"; break;
        case 2: state["operationId"] = "external-edit"; break;
        case 3: state["targetId"] = "another-target"; break;
        case 4: state["min"] = 40; break;
        case 5: state["max"] = 80; break;
        case 6: state["step"] = 2; break;
        case 7: state["kind"] = "lighting"; break;
        case 8: state["count"] = 0; state["index"] = 0; state["targetId"] = ""; state["enabled"] = false; break;
        case 9: state["ackSeq"] = 2; break;
        case 10: state["ackStatus"] = "saved"; break; // cannot continue cooling with a saved-only ACK
        case 11: state["ackStatus"] = "saved"; state["operationId"] = "changed-after-saved";
                 state["count"] = 0; state["index"] = 0; state["targetId"] = ""; state["enabled"] = false; break;
        }
        CHECK(session.receive(encode(state).c_str(), 200) == FrameResult::State);
        CHECK(!session.hasPreview()); CHECK(!session.inFlight()); CHECK(!session.canSendDesired(200));
        CHECK(session.desiredValue() == 46); CHECK(session.cancellationSerial() != cancellation);
    }
    Dial::Session session; connectResponsive(session); JsonDocument input;
    CHECK(session.adjustDesired(20, 100)); CHECK(session.adjustDesired(20, 100)); CHECK(session.adjustDesired(20, 100));
    CHECK(session.desiredValue() == 100); CHECK(!session.adjustDesired(1, 100));
    CHECK(session.adjustDesired(-20, 100)); CHECK(session.adjustDesired(-20, 100)); CHECK(session.adjustDesired(-20, 100)); CHECK(session.adjustDesired(-20, 100));
    CHECK(session.desiredValue() == 20); CHECK(!session.adjustDesired(-1, 100));
    session.disconnect(); CHECK(!session.hasPreview()); CHECK(!session.canSendDesired(100));
    connectResponsive(session); auto state = responsiveState(); state["value"] = 0;
    CHECK(session.receive(encode(state).c_str(), 100) == FrameResult::State);
    CHECK(!session.adjustDesired(-1, 100)); CHECK(session.adjustDesired(1, 100)); CHECK(session.desiredValue() == 20);
    CHECK(session.makeSet(100, input)); CHECK(session.adjustDesired(5, 101));
    session.discardQueuedDesired(); CHECK(session.inFlight()); CHECK(session.desiredValue() == 20); CHECK(!session.hasQueuedDesired());
    session.writeFailed(); CHECK(session.inFlight()); // uncertain delivery never duplicates a request
    state = responsiveState(); state["value"] = 0;
    for (uint32_t now = 600; now <= 3100; now += 500) CHECK(session.receive(encode(state).c_str(), now) == FrameResult::State);
    CHECK(session.fresh(3100)); CHECK(session.expire(3100)); // ordinary heartbeat cannot hide a missing ACK
    CHECK(!session.welcomed()); CHECK(!session.hasPreview());
    connectResponsive(session, 4000); CHECK(session.adjustDesired(1, 4000)); CHECK(session.makeSet(4000, input));
    state = responsiveState(session.inFlightSeq(), "pending"); state["busy"] = true; state["enabled"] = false;
    for (uint32_t now = 4500; now < 15000; now += 500) {
        CHECK(session.receive(encode(state).c_str(), now) == FrameResult::State);
        CHECK(!session.expire(now)); CHECK(session.inFlight()); CHECK(session.canAdjust(now));
    }
    CHECK(session.expire(17500)); CHECK(!session.hasPreview());
}
static void displayDiffTests() {
    constexpr unsigned w = 360, h = 360;
    std::vector<uint16_t> previous(w*h, 0), current(w*h, 0), panel(w*h, 0);
    unsigned calls = 0;
    auto send = [&](unsigned y, unsigned rows) {
        CHECK(y < h); CHECK(rows > 0); CHECK(y + rows <= h); ++calls;
        std::memcpy(panel.data()+y*w, current.data()+y*w, rows*w*sizeof(uint16_t));
    };
    CHECK(Dial::changedBands(current.data(), previous.data(), w, h, 16, send) == 0); CHECK(calls == 0);
    CHECK(Dial::changedBands(current.data(), nullptr, w, h, 16, send) == w*h); CHECK(calls == 1);
    for (unsigned iteration = 0; iteration < 24; ++iteration) {
        previous = current; panel = previous; calls = 0;
        // Changes at top, band edges, last partial band, disjoint bands, and
        // clearing old glyph/arc pixels all reconstruct the exact full image.
        current[(iteration*17%h)*w+(iteration*31%w)] ^= 0xffff;
        current[(h-1)*w+(w-1)] ^= 0x1234;
        const auto pixels = Dial::changedBands(current.data(), previous.data(), w, h, 16, send);
        CHECK(pixels > 0 && pixels <= w*h); CHECK(panel == current);
    }
}
static JsonDocument groupState(bool mixed = true, int value = 75, int members = 3,
                               uint32_t seq = 0, const char* status = "none") {
    auto state = responsiveState(seq, status);
    state["targetId"] = "cooling:all-fans"; state["label"] = "All fans / 3";
    state["valueMixed"] = mixed; state["memberCount"] = members;
    state["count"] = 2; state["value"] = value;
    return state;
}
static void groupSchemaTests() {
    Dial::Session legacy; connect(legacy);
    CHECK(legacy.state().memberCount == 1); CHECK(!legacy.state().valueMixed);
    auto state = groupState();
    CHECK(legacy.receive(encode(state).c_str(), 101) == FrameResult::State);
    CHECK(legacy.state().valueMixed); CHECK(legacy.state().memberCount == 3);
    CHECK(legacy.canInput(101)); // optional metadata does not force responsive negotiation
    state = stateDocument(); state["count"] = 0; state["index"] = 0;
    state["targetId"] = ""; state["enabled"] = false;
    CHECK(legacy.receive(encode(state).c_str(), 102) == FrameResult::State);
    CHECK(legacy.state().memberCount == 0); CHECK(!legacy.state().valueMixed);
    for (unsigned issue = 0; issue < 14; ++issue) {
        Dial::Session session; connectResponsive(session); auto invalid = groupState();
        switch (issue) {
        case 0: invalid["memberCount"] = 0; break;
        case 1: invalid["memberCount"] = 65; break;
        case 2: invalid["memberCount"] = -1; break;
        case 3: invalid["memberCount"] = nullptr; break;
        case 4: invalid["memberCount"] = "3"; break;
        case 5: invalid["memberCount"] = 3.5; break;
        case 6: invalid["memberCount"] = true; break;
        case 7: invalid["valueMixed"] = 1; break;
        case 8: invalid["valueMixed"] = nullptr; break;
        case 9: invalid["valueMixed"] = "true"; break;
        case 10: invalid["memberCount"] = 1; break;
        case 11: invalid.remove("memberCount"); break;
        case 12: invalid["memberCount"] = 1; invalid["valueMixed"] = false;
                 invalid["count"] = 0; invalid["index"] = 0; invalid["targetId"] = ""; invalid["enabled"] = false; break;
        case 13: invalid["memberCount"] = 0;
                 invalid["count"] = 0; invalid["index"] = 0; invalid["targetId"] = ""; invalid["enabled"] = false; break;
        }
        CHECK(session.receive(encode(invalid).c_str(), 101) == FrameResult::Rejected);
        CHECK(session.state().targetId == std::string("fan-1"));
    }
    Dial::Session session; connectResponsive(session);
    CHECK(session.receive(encode(groupState(true, 100, 64)).c_str(), 101) == FrameResult::State);
    CHECK(session.receive(encode(groupState(false, 50, 1)).c_str(), 102) == FrameResult::State);
}
static void groupFlowTests() {
    Dial::Session session; connectResponsive(session); JsonDocument input;
    CHECK(session.receive(encode(groupState()).c_str(), 101) == FrameResult::State);
    CHECK(session.desiredValue() == 75); CHECK(!session.hasPreview());
    CHECK(session.adjustDesired(1, 102)); CHECK(session.desiredValue() == 76);
    CHECK(session.makeSet(102, input)); CHECK(input["value"].as<int>() == 76);
    CHECK(session.state().valueMixed); CHECK(session.state().value == 75);
    CHECK(session.adjustDesired(9, 103)); CHECK(session.desiredValue() == 85);
    auto state = groupState(true, 75, 3, 1, "pending"); state["busy"] = true; state["enabled"] = false;
    CHECK(session.receive(encode(state).c_str(), 200) == FrameResult::State);
    CHECK(session.inFlight()); CHECK(session.canAdjust(200));
    state = groupState(false, 76, 3, 1, "confirmed");
    CHECK(session.receive(encode(state).c_str(), 300) == FrameResult::State);
    CHECK(session.canSendDesired(300)); CHECK(session.desiredValue() == 85);
    CHECK(!session.state().valueMixed); // only the whole-group completion becomes common
    CHECK(session.makeSet(300, input)); CHECK(input["value"].as<int>() == 85);
    state = groupState(false, 85, 3, 2, "confirmed");
    CHECK(session.receive(encode(state).c_str(), 400) == FrameResult::State);
    CHECK(!session.hasPreview()); CHECK(!session.inFlight());
    CHECK(session.desiredValue() == 85); CHECK(!session.canSendDesired(400));

    // A mixed group whose maximum is already 100 still applies one common 100.
    session.disconnect(); connectResponsive(session);
    CHECK(session.receive(encode(groupState(true, 100)).c_str(), 101) == FrameResult::State);
    CHECK(session.adjustDesired(1, 102)); CHECK(session.hasPreview()); CHECK(session.canSendDesired(102));
    CHECK(session.desiredValue() == 100); CHECK(session.makeSet(102, input));
    CHECK(input["value"].as<int>() == 100); CHECK(!session.adjustDesired(1, 103));
    state = groupState(false, 100, 3, session.inFlightSeq(), "confirmed");
    CHECK(session.receive(encode(state).c_str(), 200) == FrameResult::State);
    CHECK(!session.hasPreview()); CHECK(!session.canSendDesired(200));

    // Partial application, a false common-value claim, changed membership and
    // target switch must not carry a later desired value into another group.
    for (unsigned reason = 0; reason < 4; ++reason) {
        Dial::Session pending; connectResponsive(pending);
        CHECK(pending.receive(encode(groupState()).c_str(), 101) == FrameResult::State);
        CHECK(pending.adjustDesired(1, 102)); CHECK(pending.makeSet(102, input)); CHECK(pending.adjustDesired(5, 103));
        auto next = groupState(true, 76, 3, 1, "confirmed");
        if (reason == 0) { next["ackStatus"] = "rejected"; next["message"] = "Confirmed 2/3"; }
        if (reason == 1) next["memberCount"] = 4;
        if (reason == 2) { next["valueMixed"] = false; next["targetId"] = "cooling:pumps"; next["memberCount"] = 1; }
        // reason 3 deliberately leaves valueMixed true in a confirmed response.
        CHECK(pending.receive(encode(next).c_str(), 200) == FrameResult::State);
        CHECK(!pending.hasPreview()); CHECK(!pending.inFlight()); CHECK(!pending.canSendDesired(200));
        if (reason == 0) CHECK(pending.state().message == std::string("Confirmed 2/3"));
    }
}
static JsonDocument rpmState(int count = 3, int minimum = 900, int maximum = 1500,
                             uint32_t seq = 0, const char* status = "none") {
    auto state = groupState(false, 50, 3, seq, status);
    state["rpmCount"] = count;
    if (count) { state["rpmMin"] = minimum; state["rpmMax"] = maximum; }
    else { state["rpmMin"] = nullptr; state["rpmMax"] = nullptr; }
    return state;
}
static void rpmSchemaTests() {
    Dial::Session session; connectResponsive(session);
    CHECK(!session.state().rpmProvided); CHECK(session.state().rpmCount == 0);
    for (auto state : { rpmState(), rpmState(1, 0, 0), rpmState(3, 0, 1000000),
                        rpmState(1, 1000000, 1000000), rpmState(0) }) {
        CHECK(session.receive(encode(state).c_str(), 101) == FrameResult::State);
        CHECK(session.state().rpmProvided); CHECK(session.state().rpmCount == state["rpmCount"].as<int>());
        CHECK(session.state().rpmMin == (session.state().rpmCount ? state["rpmMin"].as<int>() : 0));
        CHECK(session.state().rpmMax == (session.state().rpmCount ? state["rpmMax"].as<int>() : 0));
    }
    auto state = rpmState(0); state.remove("rpmMin"); state.remove("rpmMax");
    CHECK(session.receive(encode(state).c_str(), 102) == FrameResult::State);
    CHECK(session.state().rpmProvided); CHECK(session.state().rpmCount == 0);
    state = rpmState(0); state["targetId"] = ""; state["index"] = state["count"] = state["memberCount"] = 0;
    state["enabled"] = false;
    CHECK(session.receive(encode(state).c_str(), 103) == FrameResult::State);
    CHECK(session.state().rpmProvided); CHECK(session.state().rpmCount == 0);
    state = groupState(false);
    CHECK(session.receive(encode(state).c_str(), 104) == FrameResult::State);
    CHECK(!session.state().rpmProvided); CHECK(session.state().rpmMin == 0); CHECK(session.state().rpmMax == 0);
    for (unsigned issue = 0; issue < 25; ++issue) {
        auto invalid = rpmState();
        switch (issue) {
        case 0: invalid["rpmCount"] = -1; break;
        case 1: invalid["rpmCount"] = 4; break;
        case 2: invalid["rpmCount"] = nullptr; break;
        case 3: invalid["rpmCount"] = true; break;
        case 4: invalid["rpmCount"] = 1.5; break;
        case 5: invalid["rpmCount"] = "3"; break;
        case 6: invalid.remove("rpmCount"); break;
        case 7: invalid["rpmMin"] = -1; break;
        case 8: invalid["rpmMax"] = 1000001; break;
        case 9: invalid["rpmMin"] = 1501; break;
        case 10: invalid["rpmMin"] = nullptr; break;
        case 11: invalid["rpmMax"] = nullptr; break;
        case 12: invalid.remove("rpmMin"); break;
        case 13: invalid.remove("rpmMax"); break;
        case 14: invalid["rpmMin"] = true; break;
        case 15: invalid["rpmMax"] = false; break;
        case 16: invalid["rpmMin"] = "900"; break;
        case 17: invalid["rpmMax"] = 1500.5; break;
        case 18: invalid["rpmCount"] = 1; break;
        case 19: invalid["rpmCount"] = 0; break;
        case 20: invalid = rpmState(0); invalid["rpmMin"] = 0; break;
        case 21: invalid = rpmState(0); invalid["rpmMax"] = 0; break;
        case 22: invalid["targetId"] = ""; invalid["index"] = invalid["count"] = invalid["memberCount"] = 0;
                 invalid["enabled"] = false; break;
        case 23: invalid["rpmMin"] = 2147483648LL; break;
        case 24: invalid["rpmMax"] = 2147483648LL; break;
        }
        CHECK(session.receive(encode(invalid).c_str(), 105) == FrameResult::Rejected);
        CHECK(!session.state().rpmProvided); CHECK(session.state().rpmCount == 0);
    }
    Dial::Session legacy; connect(legacy);
    state = rpmState(1, 0, 0); state["memberCount"] = 1;
    CHECK(legacy.receive(encode(state).c_str(), 105) == FrameResult::State);
    CHECK(legacy.state().rpmProvided); CHECK(legacy.state().rpmMin == 0); CHECK(legacy.canInput(105));
}
static void rpmContinuationTests() {
    Dial::Session session; connectResponsive(session); JsonDocument input;
    auto state = rpmState(); CHECK(session.receive(encode(state).c_str(), 101) == FrameResult::State);
    CHECK(session.adjustDesired(5, 102)); const auto cancellation = session.cancellationSerial();
    state["rpmMin"] = 1000; state["rpmMax"] = 1700;
    CHECK(session.receive(encode(state).c_str(), 150) == FrameResult::State);
    CHECK(session.hasPreview()); CHECK(session.desiredValue() == 55); CHECK(session.cancellationSerial() == cancellation);
    CHECK(session.makeSet(150, input)); CHECK(session.adjustDesired(8, 151));
    state = rpmState(1, 0, 0, 1, "pending"); state["busy"] = true; state["enabled"] = false;
    CHECK(session.receive(encode(state).c_str(), 200) == FrameResult::State);
    CHECK(session.inFlight()); CHECK(session.hasQueuedDesired()); CHECK(session.desiredValue() == 63);
    CHECK(session.cancellationSerial() == cancellation); CHECK(!session.canSendDesired(200));
    // RPM loss is telemetry only while the host continues to authorize the
    // same operation. A real safety/availability change still cancels normally.
    state = rpmState(0, 0, 0, 1, "pending"); state["busy"] = true; state["enabled"] = false;
    CHECK(session.receive(encode(state).c_str(), 250) == FrameResult::State);
    CHECK(session.hasQueuedDesired()); CHECK(session.desiredValue() == 63); CHECK(session.cancellationSerial() == cancellation);
    state = rpmState(3, 1200, 1900, 1, "confirmed"); state["value"] = 55;
    CHECK(session.receive(encode(state).c_str(), 300) == FrameResult::State);
    CHECK(session.canSendDesired(300)); CHECK(session.desiredValue() == 63); CHECK(session.cancellationSerial() == cancellation);
    CHECK(session.makeSet(300, input)); CHECK(input["value"].as<int>() == 63);
    state = rpmState(3, 1250, 1950, 2, "confirmed"); state["value"] = 63;
    CHECK(session.receive(encode(state).c_str(), 400) == FrameResult::State);
    CHECK(!session.hasPreview()); CHECK(!session.inFlight());
    CHECK(session.fresh(3399)); CHECK(!session.fresh(3400)); CHECK(session.expire(3400));
    CHECK(!session.hasState()); CHECK(!session.fresh(3400));
}
static JsonDocument temperatureState(uint32_t seq = 0, const char* status = "none") {
    auto state = rpmState(3, 900, 1500, seq, status);
    state["cpuTempDeciC"] = 554; state["gpuTempDeciC"] = 487; state["waterTempDeciC"] = 321;
    return state;
}
static void temperatureSchemaTests() {
    Dial::Session session; connectResponsive(session);
    CHECK(!session.state().temperaturesProvided);
    CHECK(!session.state().cpuTempValid && !session.state().gpuTempValid && !session.state().waterTempValid);
    auto state = temperatureState();
    state["cpuTempDeciC"] = 0; state["gpuTempDeciC"] = 1500;
    CHECK(session.receive(encode(state).c_str(), 101) == FrameResult::State);
    CHECK(session.state().temperaturesProvided);
    CHECK(session.state().cpuTempValid && session.state().gpuTempValid && session.state().waterTempValid);
    CHECK(session.state().cpuTempDeciC == 0 && session.state().gpuTempDeciC == 1500 && session.state().waterTempDeciC == 321);
    // Invalid telemetry rejects the whole frame without partially replacing the
    // preceding accepted sample. Integer zero is legal, unlike null/missing.
    for (const char* field : { "cpuTempDeciC", "gpuTempDeciC", "waterTempDeciC" }) {
        for (unsigned issue = 0; issue < 9; ++issue) {
            auto invalid = temperatureState();
            switch (issue) {
            case 0: invalid[field] = -1; break;
            case 1: invalid[field] = 1501; break;
            case 2: invalid[field] = true; break;
            case 3: invalid[field] = false; break;
            case 4: invalid[field] = "321"; break;
            case 5: invalid[field] = 321.5; break;
            case 6: invalid[field] = 2147483648LL; break;
            case 7: invalid[field].to<JsonArray>().add(321); break;
            case 8: invalid[field].to<JsonObject>()["value"] = 321; break;
            }
            CHECK(session.receive(encode(invalid).c_str(), 102) == FrameResult::Rejected);
            CHECK(session.state().cpuTempDeciC == 0 && session.state().gpuTempDeciC == 1500 && session.state().waterTempDeciC == 321);
        }
    }
    state = rpmState(); state["cpuTempDeciC"] = nullptr;
    CHECK(session.receive(encode(state).c_str(), 103) == FrameResult::State);
    CHECK(session.state().temperaturesProvided);
    CHECK(!session.state().cpuTempValid && !session.state().gpuTempValid && !session.state().waterTempValid);
    CHECK(session.state().cpuTempDeciC == 0 && session.state().gpuTempDeciC == 0 && session.state().waterTempDeciC == 0);
    state["waterTempDeciC"] = 0;
    CHECK(session.receive(encode(state).c_str(), 104) == FrameResult::State);
    CHECK(!session.state().cpuTempValid && !session.state().gpuTempValid && session.state().waterTempValid);
    // A fresh global sample is meaningful even without a controllable target.
    state = temperatureState(); state["targetId"] = ""; state["index"] = state["count"] = state["memberCount"] = 0;
    state["enabled"] = false; state["rpmCount"] = 0; state["rpmMin"] = state["rpmMax"] = nullptr;
    CHECK(session.receive(encode(state).c_str(), 105) == FrameResult::State);
    CHECK(session.state().temperaturesProvided && session.state().waterTempDeciC == 321);
    CHECK(!session.canAdjust(105) && !session.canInput(105, true));
    CHECK(session.receive(encode(rpmState()).c_str(), 106) == FrameResult::State);
    CHECK(!session.state().temperaturesProvided && !session.state().waterTempValid);
    Dial::Session legacy; connect(legacy);
    CHECK(legacy.receive(encode(temperatureState()).c_str(), 101) == FrameResult::State);
    CHECK(legacy.state().temperaturesProvided && legacy.canInput(101));
    legacy.disconnect(); CHECK(!legacy.state().temperaturesProvided);
    CHECK(!legacy.state().cpuTempValid && !legacy.state().gpuTempValid && !legacy.state().waterTempValid);
}
static void temperatureContinuationTests() {
    Dial::Session session; connectResponsive(session); JsonDocument input;
    auto state = temperatureState(); CHECK(session.receive(encode(state).c_str(), 101) == FrameResult::State);
    CHECK(session.adjustDesired(5, 102)); const auto cancellation = session.cancellationSerial();
    state["cpuTempDeciC"] = 600; state["gpuTempDeciC"] = nullptr; state.remove("waterTempDeciC");
    CHECK(session.receive(encode(state).c_str(), 150) == FrameResult::State);
    CHECK(session.hasPreview() && session.desiredValue() == 55 && session.cancellationSerial() == cancellation);
    CHECK(session.makeSet(150, input)); CHECK(session.adjustDesired(8, 151));
    state = temperatureState(1, "pending"); state["busy"] = true; state["enabled"] = false;
    for (unsigned update = 0; update < 3; ++update) {
        const uint32_t now = 200 + update * 500;
        if (update == 0) { state["cpuTempDeciC"] = 0; state["gpuTempDeciC"] = 1500; }
        if (update == 1) { state["cpuTempDeciC"] = nullptr; state["gpuTempDeciC"] = nullptr; state["waterTempDeciC"] = nullptr; }
        if (update == 2) { state.remove("cpuTempDeciC"); state.remove("gpuTempDeciC"); state.remove("waterTempDeciC"); }
        CHECK(session.receive(encode(state).c_str(), now) == FrameResult::State);
        CHECK(session.inFlight() && session.hasQueuedDesired() && session.desiredValue() == 63);
        CHECK(session.cancellationSerial() == cancellation && !session.canSendDesired(now));
    }
    state = temperatureState(1, "confirmed"); state["value"] = 55;
    CHECK(session.receive(encode(state).c_str(), 1300) == FrameResult::State);
    CHECK(session.canSendDesired(1300) && session.desiredValue() == 63);
    CHECK(session.makeSet(1300, input)); CHECK(input["value"].as<int>() == 63);
    state = temperatureState(2, "confirmed"); state["value"] = 63;
    CHECK(session.receive(encode(state).c_str(), 1400) == FrameResult::State);
    CHECK(!session.hasPreview() && !session.inFlight());
    state["waterTempDeciC"] = nullptr;
    CHECK(session.receive(encode(state).c_str(), 1500) == FrameResult::State);
    CHECK(session.state().ackStatus == Dial::AckStatus::Confirmed && session.state().ackSeq == 2);
    CHECK(!session.hasPreview() && !session.inFlight() && session.cancellationSerial() == cancellation);
    CHECK(session.expire(4500)); CHECK(!session.state().temperaturesProvided);
    CHECK(!session.state().cpuTempValid && !session.state().gpuTempValid && !session.state().waterTempValid);
}
int main() {
    framingTests(); encoderTests(); touchTests(); schemaTests(); sessionTests();
    responsiveSchemaTests(); responsiveFlowTests(); responsiveCancellationTests(); displayDiffTests();
    groupSchemaTests(); groupFlowTests();
    rpmSchemaTests(); rpmContinuationTests();
    temperatureSchemaTests(); temperatureContinuationTests();
    std::cout << "FlowDeck Dial core: " << checks << " checks passed\n";
    return 0;
}
