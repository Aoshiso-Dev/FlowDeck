#pragma once

#include "dial_core.h"

namespace Dial::Display {
// Presentation only: this observes completed requests and cannot release,
// cancel, repeat or otherwise change a Session's control operation.
class Feedback {
public:
    static constexpr uint32_t DurationMs = 1000;

    void reset() { visible_ = false; }
    bool visible() const { return visible_; }
    int32_t members() const { return context_.memberCount; }

    void observe(const State& previous, uint32_t previousSeq, bool previousPreview, bool previousQueued,
                 int32_t previousDesired, uint32_t previousCancellation,
                 bool previousFresh, const Session& current, uint32_t now) {
        update(current, now);
        const auto& next = current.state();
        // A preview plus an in-flight sequence identifies a set, not a tap.
        // If a later rotation remains queued, only its eventual result may
        // display a check. Heartbeats and old results have no matching flight.
        if (!previousFresh || !previousSeq || !previousPreview || previousQueued ||
            previousCancellation != current.cancellationSerial() ||
            !sameContext(previous, next) || next.kind != Kind::Cooling ||
            next.ackSeq != previousSeq || next.ackStatus != AckStatus::Confirmed ||
            next.valueMixed || next.value != previousDesired || !next.enabled || next.busy ||
            current.inFlight() || current.hasPreview()) return;
        context_ = next;
        startedAt_ = now;
        visible_ = true;
    }

    void update(const Session& current, uint32_t now) {
        const auto& state = current.state();
        if (visible_ && (!current.fresh(now) || current.hasPreview() || current.inFlight() ||
            !sameContext(context_, state) || state.value != context_.value ||
            !state.enabled || state.busy || state.valueMixed ||
            state.ackStatus == AckStatus::Rejected || state.ackStatus == AckStatus::Unverified ||
            state.ackStatus == AckStatus::Pending || state.ackStatus == AckStatus::Saved ||
            static_cast<uint32_t>(now - startedAt_) >= DurationMs)) reset();
    }

private:
    static bool sameContext(const State& a, const State& b) {
        return b.count > 0 && !std::strcmp(a.sessionId, b.sessionId) &&
            !std::strcmp(a.operationId, b.operationId) && !std::strcmp(a.targetId, b.targetId) &&
            a.kind == b.kind && a.min == b.min && a.max == b.max && a.step == b.step &&
            a.memberCount == b.memberCount && a.count == b.count && a.index == b.index;
    }
    State context_;
    uint32_t startedAt_ = 0;
    bool visible_ = false;
};

inline bool pending(const Session& session, bool fresh) {
    const auto& state = session.state();
    if (!fresh || !state.count) return false;
    if (session.hasPreview() || session.inFlight()) return true;
    if (state.ackStatus == AckStatus::Rejected || state.ackStatus == AckStatus::Unverified ||
        state.ackStatus == AckStatus::Saved) return false;
    return state.busy || state.ackStatus == AckStatus::Pending;
}

inline const char* shortStatus(const Session& session, bool fresh, bool touchReady) {
    const auto& state = session.state();
    if (!fresh) return "未接続";
    if (!state.count) return "対象なし";
    // A new local request supersedes a prior error. Without a local request,
    // a terminal ACK remains visible even if the host is still marked busy.
    if (session.hasPreview() || session.inFlight()) return "";
    if (state.ackStatus == AckStatus::Rejected) return "適用失敗";
    if (state.ackStatus == AckStatus::Unverified) return "確認不可";
    // Saving lighting settings is not a confirmation of physical output.
    if (state.ackStatus == AckStatus::Saved) return "出力未確認";
    if (pending(session, fresh)) return "";
    if (!state.enabled) return "操作不可";
    if (!touchReady) return "タッチ異常";
    return "";
}
} // namespace Dial::Display
