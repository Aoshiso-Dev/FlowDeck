// Use the same before/after observer hook as receiveFrames, while bypassing USB.
Dial::FrameResult receiveTestFrame(const char* line, uint32_t now) {
    const auto previous = session.state();
    const auto seq = session.inFlightSeq();
    const auto preview = session.hasPreview(), fresh = session.fresh(now), queued = session.hasQueuedDesired();
    const auto desired = session.desiredValue();
    const auto cancellation = session.cancellationSerial();
    const auto result = session.receive(line, now);
    if (result == Dial::FrameResult::State)
        feedback.observe(previous, seq, preview, queued, desired, cancellation, fresh, session, now);
    else if (result == Dial::FrameResult::Welcome || result == Dial::FrameResult::Identify)
        feedback.reset();
    return result;
}

// Appended after the unchanged drawing functions by render_host.py. Hardware
// APIs are replaced by a framebuffer, but font/layout/rasterization are real.
enum CaseFlag {
    Disabled = 1, Busy = 2, Mixed = 4, Preview = 8, Pending = 16,
    Stale = 32, NoTemperatures = 64, NoRpm = 128, LongText = 256,
    MaxMembers = 512, MissingTelemetry = 1024, Queued = 2048
};

void caseState(const char* name, const char* target, const char* kind,
    int value = 60, int flags = 0, uint32_t now = 100) {
    session = Dial::Session{};
    iconMotion.reset(); feedback.reset(); drawnFeedback = false;
    needsRender = true; lastRender = 0; messagePage = 0; textPageStartedAt = 100;
    lastFresh = false; canvas.texts.clear();
    receiveTestFrame(R"({"v":1,"type":"welcome","sessionId":"qa","responsiveInput":1})", 100);
    const bool noTarget = !target[0];
    const bool pump = !std::strcmp(target, "cooling:pumps");
    const int members = noTarget ? 0 : flags & MaxMembers ? 64 : pump ? 1 : 3;
    JsonDocument d;
    d["v"] = 1; d["type"] = "state"; d["sessionId"] = "qa";
    d["stateId"] = "qa-state"; d["operationId"] = "qa-operation";
    d["targetId"] = target; d["kind"] = kind;
    d["label"] = pump ? "ポンプ・1台" : "全ファン・3台";
    d["value"] = value; d["min"] = pump ? 20 : 50; d["max"] = 100; d["step"] = 1;
    d["index"] = noTarget ? 0 : pump ? 2 : 1; d["count"] = noTarget ? 0 : 2;
    d["memberCount"] = members; d["valueMixed"] = bool(flags & Mixed);
    d["enabled"] = !noTarget && !(flags & Disabled); d["busy"] = bool(flags & Busy);
    d["message"] = pump ? "確認1/1" : "確認3/3";
    d["ackSeq"] = 0; d["ackStatus"] = "none";
    if (!(flags & NoRpm)) {
        d["rpmCount"] = flags & MissingTelemetry ? 0 : members;
        if (noTarget || flags & MissingTelemetry) { d["rpmMin"] = nullptr; d["rpmMax"] = nullptr; }
        else { d["rpmMin"] = pump ? 2100 : 900; d["rpmMax"] = pump ? 2100 : 1500; }
    }
    if (!(flags & NoTemperatures)) {
        if (flags & MissingTelemetry) {
            d["cpuTempDeciC"] = nullptr; d["gpuTempDeciC"] = nullptr; d["waterTempDeciC"] = nullptr;
        } else {
            d["cpuTempDeciC"] = 654; d["gpuTempDeciC"] = 755; d["waterTempDeciC"] = 327;
        }
    }
    if (flags & Disabled) d["message"] = "操作できません。設定を確認してください。";
    if (flags & LongText) {
        std::string label, message;
        for (unsigned n = 0; n < 64; ++n) label += "界";
        for (unsigned n = 0; n < 85; ++n) message += "語";
        d["label"] = label; d["message"] = message;
    }
    auto receive = [&](uint32_t tick) {
        std::string frame; serializeJson(d, frame);
        if (receiveTestFrame(frame.c_str(), tick) != Dial::FrameResult::State) {
            std::cerr << "Invalid case: " << name; std::exit(10);
        }
        needsRender = true;
    };
    receive(100);
    if (flags & Preview) {
        if (!session.adjustDesired(10, 100)) std::exit(11);
        if (flags & Pending) {
            JsonDocument input;
            if (!session.makeSet(100, input)) std::exit(12);
            d["ackSeq"] = input["seq"]; d["ackStatus"] = "pending"; d["busy"] = true;
            receive(100);
            if (flags & Queued) session.adjustDesired(7, 100);
        }
    }
    render(100);
    canvas.texts.clear();
    if (flags & Stale) { session.expire(3101); now = 3101; }
    else if (now > 100) receive(now);
    needsRender = true; lastRender = 0; render(now); canvas.save(name);
}

void staticCases() {
    for (const char* kind : {"fan", "pump"}) {
        const char* target = !std::strcmp(kind, "fan") ? "cooling:all-fans" : "cooling:pumps";
        auto run = [&](const char* state, int value, int flags = 0) {
            const std::string name = std::string(kind) + "-" + state;
            caseState(name.c_str(), target, "cooling", value, flags);
        };
        run("0", 0); run("60", 60); run("100", 100);
        run("preview", 60, Preview);
        run("pending", 60, Preview | Pending);
        run("queued", 60, Preview | Pending | Queued);
        run("busy", 60, Disabled | Busy);
        run("disabled", 60, Disabled);
        run("stale", 60, Stale);
        run("missing-telemetry", 60, MissingTelemetry);
        run("rpm-only", 60, NoTemperatures);
        run("legacy-layout", 60, NoTemperatures | NoRpm);
        run("64", 60, MaxMembers);
    }
    caseState("fan-mixed", "cooling:all-fans", "cooling", 80, Mixed);
    caseState("fan-mixed-preview", "cooling:all-fans", "cooling", 80, Mixed | Preview);
    caseState("unknown-fan-label", "unknown", "cooling");
    caseState("similar-fan-id", "cooling:all-fans-unknown", "cooling");
    caseState("lighting-fan-id", "cooling:all-fans", "lighting");
    caseState("lighting-pump-id", "cooling:pumps", "lighting");
    caseState("no-target", "", "cooling", 0, Disabled);
    for (unsigned page = 0; page < 4; ++page) {
        const std::string unknown = "unknown-long-page-" + std::to_string(page);
        caseState(unknown.c_str(), "unknown", "cooling", 100, Disabled | LongText, 100 + page * 4000);
        const std::string fan = "fan-error-page-" + std::to_string(page);
        caseState(fan.c_str(), "cooling:all-fans", "cooling", 100, Disabled | LongText, 100 + page * 4000);
    }
}

unsigned motionChecks = 0;
JsonDocument motionReport;
void motionCheck(bool passed, const char* name) {
    ++motionChecks;
    if (!passed) { std::cerr << "Motion check failed: " << name; std::exit(20); }
}

JsonDocument motionFrame(bool pump, bool temperatures, int rpmMin = 900, int rpmMax = 1500,
                         int rpmCount = -1, bool provided = true) {
    JsonDocument d;
    d["v"] = 1; d["type"] = "state"; d["sessionId"] = "qa";
    d["stateId"] = "qa-state"; d["operationId"] = "qa-operation";
    d["targetId"] = pump ? "cooling:pumps" : "cooling:all-fans";
    d["label"] = pump ? "ポンプ・1台" : "全ファン・3台"; d["kind"] = "cooling";
    d["value"] = 60; d["min"] = 20; d["max"] = 100; d["step"] = 1;
    d["index"] = pump ? 2 : 1; d["count"] = 2; d["memberCount"] = pump ? 1 : 3;
    d["enabled"] = true; d["busy"] = false;
    d["message"] = pump ? "確認1/1" : "確認3/3";
    d["ackSeq"] = 0; d["ackStatus"] = "none";
    if (provided) {
        const int count = rpmCount < 0 ? pump ? 1 : 3 : rpmCount;
        d["rpmCount"] = count;
        if (!count) { d["rpmMin"] = nullptr; d["rpmMax"] = nullptr; }
        else { d["rpmMin"] = rpmMin; d["rpmMax"] = rpmMax; }
    }
    if (temperatures) {
        d["cpuTempDeciC"] = 654; d["gpuTempDeciC"] = 755; d["waterTempDeciC"] = 327;
    }
    return d;
}

void acceptMotionFrame(const JsonDocument& d, uint32_t now) {
    std::string encoded; serializeJson(d, encoded);
    motionCheck(receiveTestFrame(encoded.c_str(), now) == Dial::FrameResult::State, "valid motion fixture");
    needsRender = true;
}

void resetMotionCase(JsonDocument& d, uint32_t now = 100) {
    session = Dial::Session{}; iconMotion.reset(); feedback.reset(); drawnFeedback = false;
    needsRender = true; lastRender = now - 100; messagePage = 0; textPageStartedAt = now;
    lastFresh = false; drawnState = Dial::State{};
    canvas.resetMetrics();
    receiveTestFrame(R"({"v":1,"type":"welcome","sessionId":"qa","responsiveInput":1})", now);
    acceptMotionFrame(d, now); render(now);
}

std::vector<uint8_t> controlSnapshot() {
    const auto* start = reinterpret_cast<const uint8_t*>(&session.state());
    std::vector<uint8_t> bytes(start, start + sizeof(Dial::State));
    const uint32_t properties[] = {uint32_t(session.welcomed()), uint32_t(session.hasState()),
        uint32_t(session.responsive()), uint32_t(session.hasPreview()), uint32_t(session.inFlight()),
        uint32_t(session.hasQueuedDesired()), uint32_t(session.desiredValue()), session.inFlightSeq(),
        session.cancellationSerial()};
    const auto* props = reinterpret_cast<const uint8_t*>(properties);
    bytes.insert(bytes.end(), props, props + sizeof(properties)); return bytes;
}

bool outsideIconEqual(const std::vector<uint16_t>& a, const std::vector<uint16_t>& b, bool temperatures) {
    const int cy = temperatures ? 61 : 67;
    for (int y = 0; y < 360; ++y) for (int x = 0; x < 360; ++x)
        if ((x < 161 || x > 199 || y < cy - 19 || y > cy + 19) && a[y * 360 + x] != b[y * 360 + x]) return false;
    return true;
}

void motionSequence(const char* name, bool pump, bool temperatures, int flags = 0) {
    auto d = motionFrame(pump, temperatures, pump ? 2100 : 900, pump ? 2100 : 1500);
    if (flags & Disabled) d["enabled"] = false;
    if (flags & Busy) d["busy"] = true;
    resetMotionCase(d);
    if (flags & Preview) {
        motionCheck(session.adjustDesired(10, 100), "preview starts before animation");
        if (flags & Pending) {
            JsonDocument input; motionCheck(session.makeSet(100, input), "set is queued before animation");
            d["ackSeq"] = input["seq"]; d["ackStatus"] = "pending"; d["busy"] = true;
            acceptMotionFrame(d, 100);
            if (flags & Queued) motionCheck(session.adjustDesired(7, 100), "newest desired remains queued");
        }
        needsRender = true; render(140);
    }
    const uint32_t begin = flags & Preview ? 140 : 100;
    const auto initial = canvas.pixels;
    const auto controls = controlSnapshot();
    const uint64_t initialTransfers = panel.transferred;
    const uint32_t firstPhase = iconMotion.phase();
    unsigned changedFrames = 0;
    uint64_t maximumWrites = 0, maximumTransfer = 0;
    auto previous = initial;
    const int cy = temperatures ? 61 : 67;
    for (unsigned frame = 1; frame <= 60; ++frame) {
        const auto beforeTransfer = panel.transferred;
        canvas.resetMetrics(); render(begin + frame * 40);
        motionCheck(panel.pixels == canvas.pixels, "packed partial transfer reconstructs full framebuffer");
        motionCheck(panel.rotationMatches(), "all packed animation pixels rotate by 180 degrees in actual driver path");
        motionCheck(outsideIconEqual(initial, canvas.pixels, temperatures), "motion changes no text, number, arc, or telemetry pixels");
        if (pump) {
            bool sameSilhouette = true;
            for (int y = cy - 19; y <= cy + 19; ++y) for (int x = 161; x <= 199; ++x)
                if ((initial[y * 360 + x] == Background) != (canvas.pixels[y * 360 + x] == Background)) sameSilhouette = false;
            motionCheck(sameSilhouette, "pump stream remains inside the unchanged drop silhouette");
        }
        motionCheck(controlSnapshot() == controls, "motion leaves session state and pending input untouched");
        if (previous != canvas.pixels) ++changedFrames;
        if (canvas.writes) {
            motionCheck(canvas.minX >= 161 && canvas.maxX <= 199 && canvas.minY >= cy - 19 && canvas.maxY <= cy + 19,
                "animation draw writes stay inside icon bounds");
            motionCheck(canvas.texts.empty(), "animation performs no font/text redraw");
        }
        motionCheck(panel.transferred - beforeTransfer <= 39 * 39, "animation transfer capped at one 39 square icon");
        motionCheck(panel.bands.size() <= 1, "at most one transfer per animation frame");
        for (const auto& b : panel.bands)
            motionCheck(b.x >= 161 && b.x + b.w <= 200 && b.y >= cy - 19 && b.y + b.h <= cy + 20,
                "packed transfer bounds stay inside icon");
        maximumWrites = std::max(maximumWrites, canvas.writes);
        maximumTransfer = std::max(maximumTransfer, panel.transferred - beforeTransfer);
        if (frame == 1 || frame == 8 || frame == 16 || frame == 24 || frame == 40 || frame == 60) {
            const std::string imageName = std::string(name) + "-" + std::to_string(frame);
            canvas.save(imageName.c_str());
        }
        // Compare each incremental frame against a freshly cleared rendering
        // of this same phase. This catches trails and clipping in all phases.
        const auto incremental = canvas.pixels;
        canvas.fillRect(161, cy - 19, 39, 39, Background);
        modeIcon(pump ? DisplayMode::Pumps : DisplayMode::Fans, 180, cy,
            pump ? PumpAccent : FanAccent, iconMotion.phase(), iconMotion.active());
        motionCheck(canvas.pixels == incremental, "incremental icon matches freshly rendered phase without trails");
        previous = incremental;
    }
    motionCheck(changedFrames > 30 && iconMotion.phase() != firstPhase, "RPM animation visibly advances");
    auto item = motionReport["sequences"].as<JsonArray>().add<JsonObject>();
    item["name"] = name; item["frames"] = 60; item["changedFrames"] = changedFrames;
    item["maxPixelWriteCalls"] = maximumWrites; item["maxTransferredPixels"] = maximumTransfer;
    item["totalTransferredPixels"] = panel.transferred - initialTransfers;
}

void stationarySequence(const char* name, JsonDocument d) {
    resetMotionCase(d); const auto initial = canvas.pixels; const auto before = panel.transferred;
    for (uint32_t now = 140; now <= 2500; now += 40) {
        canvas.resetMetrics(); render(now);
        motionCheck(!iconMotion.active(), "invalid or zero RPM stops animation");
        motionCheck(canvas.pixels == initial && canvas.writes == 0 && panel.transferred == before,
            "stopped animation consumes no drawing or transfer work");
    }
    canvas.save(name);
}

void motionPolicyTests() {
    using namespace Dial::Display;
    auto d = motionFrame(false, true); resetMotionCase(d); auto state = session.state();
    motionCheck(completeRpm(state, true) && measuredRpm(state) == 1200, "group uses range midpoint");
    state.rpmMin = 901; state.rpmMax = 1500;
    motionCheck(measuredRpm(state) == 1200, "odd range midpoint safely truncates");
    for (const auto m : {Mode::Fans, Mode::Pumps}) {
        motionCheck(rateMilliTurns(m, 0) == 0, "zero RPM rate is zero");
        uint32_t previous = 0;
        for (uint32_t rpm = 1; rpm <= 1000000; rpm += 137) {
            const auto rate = rateMilliTurns(m, rpm);
            motionCheck(rate >= previous && rate <= (m == Mode::Fans ? 1200u : 800u), "rate monotonic and capped");
            previous = rate;
        }
    }
    state = session.state(); Motion clock; clock.update(state, true, 100);
    clock.update(state, true, 140); const auto first = clock.phase();
    motionCheck(first == 40 * rateMilliTurns(Mode::Fans, 1200), "phase uses elapsed milliseconds and measured RPM");
    clock.update(state, true, 100000);
    motionCheck(clock.phase() == first, "long suspension does not replay skipped animation");
    clock.update(state, true, 100040);
    motionCheck(clock.phase() == first * 2, "normal motion resumes after suspension");
    clock.reset(); clock.update(state, true, 0xfffffff0u); clock.update(state, true, 24u);
    motionCheck(clock.phase() == first, "millis wrap advances exactly 40 milliseconds");
    state.value = 100; state.enabled = false; state.busy = true;
    clock.update(state, true, 64u);
    motionCheck(clock.phase() == first * 2 && clock.active(), "requested percent and busy status do not drive measured motion");
    const auto movingPhase = clock.phase(); state.rpmCount = 2;
    clock.update(state, true, 104u);
    motionCheck(!clock.active() && clock.phase() == movingPhase, "partial group telemetry freezes phase");
    state.rpmCount = 3; clock.update(state, true, 144u);
    motionCheck(clock.active() && clock.phase() == movingPhase, "restored telemetry has no catch-up jump");
    state.memberCount = 2; state.rpmCount = 2; clock.update(state, true, 184u);
    motionCheck(clock.active() && clock.phase() == 0, "member change starts a new animation");
    std::strcpy(state.targetId, "cooling:pumps"); state.memberCount = state.rpmCount = 1;
    clock.update(state, true, 224u);
    motionCheck(clock.currentMode() == Mode::Pumps && clock.phase() == 0, "mode switch resets animation");
    clock.update(state, false, 264u);
    motionCheck(clock.currentMode() == Mode::Neutral && !clock.active() && clock.phase() == 0, "stale state resets animation");
    clock.update(state, true, 304u);
    motionCheck(clock.currentMode() == Mode::Pumps && clock.phase() == 0, "fresh connection restarts from fixed phase");
}

void fullReference(uint32_t now) {
    const auto incremental = canvas.pixels;
    motionCheck(panel.pixels == incremental, "transition transfer reconstructs full framebuffer");
    needsRender = true; lastRender = now - 40; render(now);
    motionCheck(canvas.pixels == incremental, "incremental transition equals clean full redraw");
    motionCheck(panel.pixels == incremental, "normal band transfer remains exact after icon transfers");
}

void motionTransitions() {
    auto d = motionFrame(false, true); resetMotionCase(d);
    const auto before = animationFrameCount;
    canvas.resetMetrics(); render(139);
    motionCheck(animationFrameCount == before && canvas.writes == 0, "animation respects shared 40 millisecond budget");
    render(140);
    motionCheck(animationFrameCount == before + 1, "next animation runs at shared frame boundary");
    fullReference(140);
    const auto fanPhase = iconMotion.phase();
    render(1000);
    motionCheck(iconMotion.phase() == fanPhase, "renderer does not catch up after a long pause");
    fullReference(1000);
    d = motionFrame(true, true, 2100, 2100); acceptMotionFrame(d, 1040); render(1040);
    motionCheck(iconMotion.phase() == 0 && iconMotion.currentMode() == DisplayMode::Pumps, "renderer resets fan phase on pump switch");
    fullReference(1040); canvas.save("motion-switch-to-pump");
    render(1080); fullReference(1080);
    d.remove("cpuTempDeciC"); d.remove("gpuTempDeciC"); d.remove("waterTempDeciC");
    acceptMotionFrame(d, 1120); render(1120); fullReference(1120);
    canvas.save("motion-temperature-layout-change");
    render(1160); fullReference(1160);
    d["rpmMin"] = d["rpmMax"] = 0;
    acceptMotionFrame(d, 1200); render(1200); fullReference(1200);
    const auto stopped = canvas.pixels;
    canvas.resetMetrics(); render(1240);
    motionCheck(!iconMotion.active() && canvas.pixels == stopped && canvas.writes == 0, "RPM falling to zero stops actual renderer");
    d["rpmMin"] = d["rpmMax"] = 5000;
    acceptMotionFrame(d, 1280); render(1280); fullReference(1280);
    const auto resumedPhase = iconMotion.phase(); render(1320); fullReference(1320);
    motionCheck(iconMotion.phase() == (resumedPhase + 40 * 800) % 1000000, "high measured RPM changes actual phase speed");
    canvas.save("motion-pump-fast");
    d = motionFrame(false, true, 100, 100); acceptMotionFrame(d, 1360); render(1360);
    render(1400); fullReference(1400);
    motionCheck(iconMotion.phase() == 40 * Dial::Display::rateMilliTurns(DisplayMode::Fans, 100), "low measured RPM changes actual phase speed");
    canvas.save("motion-fan-slow");
    d["rpmCount"] = 2; acceptMotionFrame(d, 1440); render(1440); fullReference(1440);
    const auto partial = canvas.pixels;
    canvas.resetMetrics(); render(1480);
    motionCheck(!iconMotion.active() && canvas.pixels == partial && canvas.writes == 0, "partial telemetry transition freezes actual renderer");
    d = motionFrame(false, true); resetMotionCase(d, 0xfffffff0u);
    render(24u); fullReference(24u);
    motionCheck(iconMotion.phase() == 40 * Dial::Display::rateMilliTurns(DisplayMode::Fans, 1200), "actual render handles millis wrap without flicker or skipped cadence");
    canvas.save("motion-millis-wrap");
    // A failed optional previous-frame allocation keeps animation bounded and
    // correctly falls back to a full transfer for the regular UI path.
    previousFramebuffer = nullptr; previousFramebufferValid = false;
    resetMotionCase(d); render(140); fullReference(140);
    motionCheck(panel.pixels == canvas.pixels, "animation works without optional previous-frame memory");
    previousFramebuffer = previousStorage.data(); previousFramebufferValid = false;
}

void motionCases() {
    motionReport["sequences"].to<JsonArray>();
    motionPolicyTests();
    motionSequence("motion-fan-temps", false, true);
    motionSequence("motion-pump-temps", true, true);
    motionSequence("motion-fan-rpm-only", false, false);
    motionSequence("motion-pump-rpm-only", true, false);
    motionSequence("motion-fan-preview", false, true, Preview);
    motionSequence("motion-fan-queued", false, true, Preview | Pending | Queued);
    motionSequence("motion-pump-pending", true, true, Preview | Pending);
    motionSequence("motion-pump-busy", true, true, Busy | Disabled);
    stationarySequence("motion-fan-zero", motionFrame(false, true, 0, 0));
    stationarySequence("motion-pump-zero", motionFrame(true, true, 0, 0));
    stationarySequence("motion-fan-partial", motionFrame(false, true, 900, 1500, 2));
    stationarySequence("motion-fan-missing", motionFrame(false, true, 0, 0, 0));
    stationarySequence("motion-pump-missing", motionFrame(true, true, 0, 0, 0));
    stationarySequence("motion-legacy-no-rpm", motionFrame(false, false, 0, 0, 0, false));
    motionTransitions();
    auto d = motionFrame(false, true); resetMotionCase(d); render(140);
    motionCheck(session.expire(3100), "fixture session expires");
    needsRender = true; render(3100); const auto stale = canvas.pixels;
    canvas.resetMetrics(); render(3140);
    motionCheck(canvas.pixels == stale && canvas.writes == 0 && !iconMotion.active(), "expired session keeps no residual animation");
    canvas.save("motion-expired");
    motionReport["passed"] = true; motionReport["checks"] = motionChecks;
    motionReport["hardwareAccess"] = false;
    std::string json; serializeJson(motionReport, json);
    std::ofstream("motion-result.json") << json;
}


void quietRender(uint32_t now) { canvas.resetMetrics(); render(now); }

unsigned quietChecks = 0;
void quietCheck(bool pass, const char* name) {
    ++quietChecks;
    if (!pass) { std::cerr << "Quiet display check failed: " << name; std::exit(40); }
}

bool hasText(const char* text) {
    for (const auto& t : canvas.texts) if (t.text == text) return true;
    return false;
}

JsonDocument beginSet(bool pump = false, uint32_t start = 100) {
    auto d = motionFrame(pump, true, 0, 0);
    resetMotionCase(d, start);
    quietCheck(!feedback.visible(), "initial connected state has no success marker");
    quietCheck(session.adjustDesired(10, start), "real immediate preview begins");
    JsonDocument input;
    quietCheck(session.makeSet(start, input), "real set becomes single flight");
    d["ackSeq"] = input["seq"]; d["ackStatus"] = "pending"; d["busy"] = true;
    acceptMotionFrame(d, start + 40); quietRender(start + 40);
    quietCheck(!feedback.visible() && Dial::Display::pending(session, true), "pending has dashed visual state without success");
    return d;
}
void finishSet(JsonDocument& d, uint32_t now = 180) {
    d["ackStatus"] = "confirmed"; d["busy"] = false; d["value"] = 70;
    acceptMotionFrame(d, now); quietRender(now);
}

void quietFeedbackTests() {
    for (bool pump : {false, true}) {
        auto d = beginSet(pump);
        canvas.save(pump ? "quiet-pump-pending" : "quiet-fan-pending");
        finishSet(d);
        quietCheck(feedback.visible() && feedback.members() == (pump ? 1 : 3), "latest full confirmation displays exact group count");
        quietCheck(!session.hasPreview() && !session.inFlight(), "UI observes existing completed control state");
        canvas.save(pump ? "quiet-pump-confirmed" : "quiet-fan-confirmed");
        const auto confirmed = canvas.pixels;
        d["stateId"] = "heartbeat-confirmed";
        acceptMotionFrame(d, 900); quietRender(900);
        canvas.resetMetrics(); quietRender(1179);
        quietCheck(feedback.visible() && canvas.pixels == confirmed && canvas.writes == 0, "check remains for 999 milliseconds with zero RPM and no redraw");
        quietRender(1180);
        quietCheck(!feedback.visible() && canvas.pixels != confirmed && canvas.writes > 0,
            "check expires exactly 1000 milliseconds and redraws with stopped animation");
        quietCheck(panel.pixels == canvas.pixels && panel.rotationMatches(), "expired check is erased in full reconstructed rotated panel");
        canvas.save(pump ? "quiet-pump-expired" : "quiet-fan-expired");
        acceptMotionFrame(d, 1300); quietRender(1300);
        quietCheck(!feedback.visible(), "repeated confirmed heartbeat never starts or extends success");
    }
    for (const char* status : {"rejected", "unverified", "saved"}) {
        auto d = beginSet(); d["ackStatus"] = status; d["busy"] = false;
        d["message"] = "機器の適用結果を確認できませんでした。詳細はPC側で確認してください。";
        acceptMotionFrame(d, 180); quietRender(180);
        quietCheck(!feedback.visible(), "rejected unverified and saved are never physical output success");
        const char* text = !std::strcmp(status, "rejected") ? "適用失敗" : !std::strcmp(status, "unverified") ? "確認不可" : "出力未確認";
        quietCheck(hasText(text), "failure uses bounded short notification");
        canvas.save((std::string("quiet-") + status).c_str());
    }
    for (const char* status : {"rejected", "unverified"}) {
        auto d = motionFrame(false, true, 900, 1500);
        d["ackSeq"] = 42; d["ackStatus"] = status; d["busy"] = true;
        d["valueMixed"] = true;
        resetMotionCase(d);
        quietCheck(!feedback.visible() && hasText(!std::strcmp(status, "rejected") ? "適用失敗" : "確認不可"),
            "terminal error remains visible with host busy but no local operation");
        if (!std::strcmp(status, "unverified")) quietCheck(hasText("--") && !hasText("60") && !hasText("混在") &&
            hasText("900-1500 RPM") && hasText("CPU") && hasText("GPU") && hasText("水温"),
            "unverified output is unknown while valid RPM and all temperatures remain visible");
        canvas.save((std::string("quiet-busy-") + status).c_str());
        d["busy"] = false; acceptMotionFrame(d, 140); quietRender(140);
        quietCheck(session.adjustDesired(10, 180), "new local intent after error can start normally");
        quietRender(180);
        quietCheck(!hasText("適用失敗") && !hasText("確認不可") && hasText("70"),
            "new local preview replaces previous error with pending output");
        canvas.save((std::string("quiet-retry-") + status).c_str());
    }
    for (const char* mutation : {"mixed", "disabled", "busy", "wrong-value", "operation", "target", "members", "min", "max", "step", "kind", "index", "count"}) {
        auto d = beginSet(); d["ackStatus"] = "confirmed"; d["busy"] = false; d["value"] = 70;
        if (!std::strcmp(mutation, "mixed")) d["valueMixed"] = true;
        else if (!std::strcmp(mutation, "disabled")) d["enabled"] = false;
        else if (!std::strcmp(mutation, "busy")) d["busy"] = true;
        else if (!std::strcmp(mutation, "wrong-value")) d["value"] = 65;
        else if (!std::strcmp(mutation, "operation")) d["operationId"] = "new-operation";
        else if (!std::strcmp(mutation, "target")) d["targetId"] = "cooling:pumps";
        else if (!std::strcmp(mutation, "members")) {d["memberCount"] = 2; d["rpmCount"] = 2;}
        else if (!std::strcmp(mutation, "min")) d["min"] = 30;
        else if (!std::strcmp(mutation, "max")) d["max"] = 80;
        else if (!std::strcmp(mutation, "step")) d["step"] = 2;
        else if (!std::strcmp(mutation, "kind")) d["kind"] = "lighting";
        else if (!std::strcmp(mutation, "index")) d["index"] = 2;
        else if (!std::strcmp(mutation, "count")) d["count"] = 3;
        acceptMotionFrame(d, 180); quietRender(180);
        quietCheck(!feedback.visible(), (std::string("unsafe confirmation rejected: ") + mutation).c_str());
        if (!std::strcmp(mutation, "mixed")) canvas.save("quiet-partially-confirmed-mixed");
    }
    auto d = beginSet();
    quietCheck(session.adjustDesired(7, 150), "later rotation is queued");
    finishSet(d);
    quietCheck(!feedback.visible() && session.hasPreview(), "old successful value with newer desired value is not success");
    canvas.save("quiet-queued-old-result");
    JsonDocument newest; quietCheck(session.makeSet(220, newest), "latest queued set can be sent normally");
    d["ackStatus"] = "confirmed"; d["value"] = 70;
    acceptMotionFrame(d, 260); quietRender(260);
    quietCheck(!feedback.visible() && session.inFlight(), "older ACK leaves newer set pending without success");
    d["ackSeq"] = newest["seq"]; d["value"] = 77;
    acceptMotionFrame(d, 300); quietRender(300);
    quietCheck(feedback.visible() && session.state().value == 77, "only latest queued request gets success");
    canvas.save("quiet-queued-latest-result");
    quietCheck(session.adjustDesired(-3, 340), "new rotation after success begins normally");
    quietRender(340); quietCheck(!feedback.visible(), "new user intent immediately clears previous success");

    d = beginSet();
    quietCheck(session.adjustDesired(7, 150), "queued intent fixture begins");
    d["ackStatus"] = "confirmed"; d["busy"] = false; d["value"] = 77;
    acceptMotionFrame(d, 180); quietRender(180);
    quietCheck(!feedback.visible(), "old ACK coincidentally matching queued value does not confirm unsent request");

    d = beginSet(); d["ackStatus"] = "confirmed"; d["ackSeq"] = session.inFlightSeq() + 1; d["busy"] = false; d["value"] = 70;
    acceptMotionFrame(d, 180); quietRender(180);
    quietCheck(!feedback.visible(), "future ACK cannot impersonate completed current set");
    d = motionFrame(false, true, 0, 0); resetMotionCase(d);
    JsonDocument press; quietCheck(session.makeInput(true, 0, 100, press), "real tap input begins");
    d["ackSeq"] = press["seq"]; d["ackStatus"] = "confirmed";
    acceptMotionFrame(d, 140); quietRender(140);
    quietCheck(!feedback.visible(), "tap acknowledgement never claims fan output success");

    d = beginSet(); finishSet(d);
    quietCheck(session.makeInput(true, 0, 220, press), "tap after successful set begins");
    quietRender(220); quietCheck(!feedback.visible(), "tap clears prior success immediately");
    for (const char* nextStatus : {"pending", "saved"}) {
        d = beginSet(); finishSet(d);
        d["ackStatus"] = nextStatus; d["busy"] = false;
        acceptMotionFrame(d, 220); quietRender(220);
        quietCheck(!feedback.visible(), "external pending or saved status clears prior physical success");
        d["ackStatus"] = "confirmed";
        acceptMotionFrame(d, 260); quietRender(260);
        quietCheck(!feedback.visible(), "old success cannot reappear after pending or saved notification ends");
    }
    d = beginSet(); finishSet(d);
    d["value"] = 65; acceptMotionFrame(d, 220); quietRender(220);
    quietCheck(!feedback.visible() && hasText("65"), "external value change removes stale success immediately");
    d = beginSet(); finishSet(d);
    d["targetId"] = "cooling:pumps"; acceptMotionFrame(d, 220); quietRender(220);
    quietCheck(!feedback.visible(), "target change clears visible success");
    d = beginSet(); finishSet(d);
    receiveTestFrame(R"({"v":1,"type":"welcome","sessionId":"new-session","responsiveInput":1})", 220);
    needsRender = true; quietRender(220);
    quietCheck(!feedback.visible(), "new session clears visible success");
    d = beginSet(); finishSet(d);
    session.expire(3180); needsRender = true; quietRender(3180);
    quietCheck(!feedback.visible() && hasText("未接続"), "expired session clears success and only shows disconnected error");
    d = beginSet(); finishSet(d);
    touchReady = false; needsRender = true; quietRender(220);
    quietCheck(hasText("タッチ異常"), "touch failure remains readable even after successful output");
    canvas.save("quiet-touch-error"); touchReady = true;

    d = motionFrame(false, true, 0, 0); d["ackStatus"] = "confirmed"; d["ackSeq"] = 45;
    resetMotionCase(d); quietCheck(!feedback.visible(), "initial old confirmed ACK is not a recent user success");
    d = beginSet(false, 0xffffff80u); finishSet(d, 0xffffffd0u);
    quietCheck(feedback.visible(), "success starts near millis wrap");
    quietRender(951u); quietCheck(feedback.visible(), "check stays visible across millis wrap at 999 milliseconds");
    quietRender(952u); quietCheck(!feedback.visible(), "check expires across millis wrap after 1000 milliseconds");

    // The real ST77916 and TFT functions, with only the physical bus replaced,
    // must clip every edge and send full, band, and odd-sized icon transfers in
    // the same orientation. Distinct pixels make double rotation detectable.
    DriverBus bus; Arduino_ST77916 driver(&bus); driver.setRotation(2);
    quietCheck(bus.madctl == 0xC0 && driver.width() == 360 && driver.height() == 360,
        "actual rotation 2 emits MX|MY and preserves square dimensions");
    const struct { int x, y, w, h; } windows[] = {{0,0,360,360},{0,32,360,16},{161,42,39,39},{-3,10,8,4},{356,10,8,4},{10,-3,4,8},{10,356,4,8}};
    for (const auto& win : windows) {
        std::fill(bus.physical.begin(), bus.physical.end(), 0xFFFF);
        std::vector<uint16_t> input(win.w * win.h);
        for (unsigned i = 0; i < input.size(); ++i) input[i] = uint16_t(i * 7919u + 17u);
        driver.draw16bitRGBBitmap(win.x, win.y, input.data(), win.w, win.h);
        bool allPixels = true;
        for (int y = 0; y < 360; ++y) for (int x = 0; x < 360; ++x) {
            uint16_t expected = 0xFFFF;
            if (x >= win.x && x < win.x + win.w && y >= win.y && y < win.y + win.h)
                expected = input[(y - win.y) * win.w + x - win.x];
            if (bus.physical[(359-y)*360+359-x] != expected) allPixels = false;
        }
        quietCheck(allPixels, "actual full band icon and clipped edge transfer preserve every rotated pixel");
    }
    JsonDocument report;
    report["passed"] = true; report["checks"] = quietChecks;
    report["hardwareAccess"] = false; report["rotation"] = 180;
    report["actualDriverFunctionsTested"] = true;
    report["durationMs"] = Dial::Display::Feedback::DurationMs;
    std::string json; serializeJson(report, json); std::ofstream("quiet-result.json") << json;
}


unsigned readabilityChecks = 0;
void readabilityCheck(bool pass, const char* name) {
    ++readabilityChecks;
    if (!pass) { std::cerr << "Readable telemetry check failed: " << name; std::exit(60); }
}
void readableTelemetryCases() {
    auto save = [&](const char* name, JsonDocument d) {
        resetMotionCase(d);
        const auto controls = controlSnapshot();
        needsRender = true; quietRender(140);
        readabilityCheck(controlSnapshot() == controls, "enlarged drawing does not change control state");
        readabilityCheck(panel.pixels == canvas.pixels && panel.rotationMatches(), "enlarged telemetry reconstructs exact rotated output");
        canvas.save(name);
    };
    auto d = motionFrame(false, true, 10000, 12000); d["value"] = 100;
    save("telemetry-fan-wide-rpm", d);
    d = motionFrame(false, true, 999999, 1000000);
    save("telemetry-fan-max-range", d);
    d = motionFrame(true, true, 1000000, 1000000);
    save("telemetry-pump-max-rpm", d);
    for (bool pump : {false, true}) {
        d = motionFrame(pump, true, 0, 0); d["value"] = 0;
        d["cpuTempDeciC"] = 0; d["gpuTempDeciC"] = 0; d["waterTempDeciC"] = 0;
        save(pump ? "telemetry-pump-zero" : "telemetry-fan-zero", d);
        d["cpuTempDeciC"] = 1500; d["gpuTempDeciC"] = 1500; d["waterTempDeciC"] = 1500;
        save(pump ? "telemetry-pump-max-temps" : "telemetry-fan-max-temps", d);
        d["cpuTempDeciC"] = 999; d["gpuTempDeciC"] = 1499; d["waterTempDeciC"] = 999;
        save(pump ? "telemetry-pump-rounding" : "telemetry-fan-rounding", d);
        d["cpuTempDeciC"] = nullptr; d["gpuTempDeciC"] = nullptr; d["waterTempDeciC"] = nullptr;
        save(pump ? "telemetry-pump-missing" : "telemetry-fan-missing", d);
    }
    d = motionFrame(false, true, 900, 1500); d["waterTempDeciC"] = 325;
    save("telemetry-water-32-5", d);
    d["waterTempDeciC"] = 330; save("telemetry-water-33-0", d);
    d = motionFrame(false, true, 10000, 12000);
    d["cpuTempDeciC"] = 0; d["gpuTempDeciC"] = nullptr; d["waterTempDeciC"] = 1;
    save("telemetry-independent-missing", d);
    d["memberCount"] = 64; d["rpmCount"] = 0; d["rpmMin"] = nullptr; d["rpmMax"] = nullptr;
    save("telemetry-missing-rpm-64", d);
    d = motionFrame(false, true, 10000, 12000);
    d["cpuTempDeciC"] = 1500; d["gpuTempDeciC"] = 1500; d["waterTempDeciC"] = 1499;
    d["ackSeq"] = 1; d["ackStatus"] = "unverified";
    save("telemetry-max-unverified", d);
    d["ackStatus"] = "rejected";
    save("telemetry-max-rejected", d);
    d = motionFrame(false, false, 999999, 1000000);
    save("telemetry-legacy-max-range", d);

    // Temperature admission is intentionally unchanged: negative / out-of-range
    // data cannot replace the last valid display or alter pending controls.
    d = motionFrame(false, true, 0, 0); resetMotionCase(d);
    const auto validPixels = canvas.pixels; const auto validControls = controlSnapshot();
    for (const char* field : {"cpuTempDeciC", "gpuTempDeciC", "waterTempDeciC"}) {
        for (int invalid : {-1, -1000, 1501}) {
            auto bad = d; bad[field] = invalid; std::string encoded; serializeJson(bad, encoded);
            readabilityCheck(receiveTestFrame(encoded.c_str(), 140) != Dial::FrameResult::State,
                "negative and excessive temperatures are rejected rather than fabricated");
            quietRender(140);
            readabilityCheck(controlSnapshot() == validControls && canvas.pixels == validPixels,
                "invalid telemetry cannot change output preview or displayed measurements");
        }
    }
    JsonDocument report; report["passed"] = true; report["checks"] = readabilityChecks;
    report["hardwareAccess"] = false;
    std::string json; serializeJson(report, json); std::ofstream("readability-result.json") << json;
}

int main() { staticCases(); motionCases(); quietFeedbackTests(); readableTelemetryCases(); }
