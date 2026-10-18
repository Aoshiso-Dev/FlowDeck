#pragma once

#include <cstdint>
#include <cstring>
#include "dial_core.h"

// Presentation only: this clock never changes session state, input permission,
// requested output, or application/readback acknowledgement.
namespace Dial { namespace Display {

enum class Mode { Neutral, Fans, Pumps };

inline Mode mode(const State& state, bool fresh) {
    if (!fresh || state.count == 0 || state.kind != Kind::Cooling) return Mode::Neutral;
    if (!std::strcmp(state.targetId, "cooling:all-fans")) return Mode::Fans;
    if (!std::strcmp(state.targetId, "cooling:pumps")) return Mode::Pumps;
    return Mode::Neutral;
}

inline bool completeRpm(const State& state, bool fresh) {
    return mode(state, fresh) != Mode::Neutral && state.rpmProvided &&
        state.memberCount > 0 && state.rpmCount == state.memberCount &&
        state.rpmMin >= 0 && state.rpmMax >= state.rpmMin && state.rpmMax <= 1000000;
}

inline uint32_t measuredRpm(const State& state) {
    // The midpoint of the reported range is an animation proxy, not a mean
    // across group members. The protocol deliberately does not supply a mean.
    return static_cast<uint32_t>(state.rpmMin + (state.rpmMax - state.rpmMin) / 2);
}

inline uint32_t rateMilliTurns(Mode mode, uint32_t rpm) {
    if (rpm == 0 || mode == Mode::Neutral) return 0;
    if (mode == Mode::Fans) {
        const uint32_t bounded = rpm < 3000 ? rpm : 3000;
        return 300 + bounded * 900 / 3000;
    }
    // A stream cycle lasts 1.25-4 seconds; it indicates pump rotation, not flow.
    const uint32_t bounded = rpm < 5000 ? rpm : 5000;
    return 250 + bounded * 550 / 5000;
}

class Motion {
public:
    void reset() {
        mode_ = Mode::Neutral;
        members_ = 0;
        phase_ = lastAt_ = rate_ = 0;
        seen_ = false;
    }

    void update(const State& state, bool fresh, uint32_t now) {
        const Mode nextMode = mode(state, fresh);
        const uint32_t nextRate = completeRpm(state, fresh) ?
            rateMilliTurns(nextMode, measuredRpm(state)) : 0;
        if (!seen_ || nextMode != mode_ || state.memberCount != members_) {
            phase_ = 0;
        } else {
            // Unsigned subtraction handles millis() wrap. A long loop pause
            // must not replay skipped motion or create a large visual jump.
            const uint32_t elapsed = now - lastAt_;
            if (elapsed <= 250 && rate_ != 0 && nextRate != 0)
                phase_ = (phase_ + elapsed * rate_) % 1000000;
        }
        mode_ = nextMode;
        members_ = state.memberCount;
        rate_ = nextRate;
        lastAt_ = now;
        seen_ = true;
    }

    uint32_t phase() const { return phase_; }
    bool active() const { return rate_ != 0; }
    Mode currentMode() const { return mode_; }

private:
    Mode mode_ = Mode::Neutral;
    int32_t members_ = 0;
    uint32_t phase_ = 0, lastAt_ = 0, rate_ = 0;
    bool seen_ = false;
};

}} // namespace Dial::Display
