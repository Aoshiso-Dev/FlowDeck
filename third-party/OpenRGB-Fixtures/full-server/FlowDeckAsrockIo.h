/* Checked, bounded ASRock exchanges for FlowDeck's pinned OpenRGB helper.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <functional>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <utility>

enum class FlowDeckAsrockIoPhase { BeforeWrite, BeforeRead, AfterRead };
enum class FlowDeckAsrockIoStage { Ready, WriteUncertain, ReplyUncertain, ReplyReceived };
struct FlowDeckAsrockIoSnapshot
{
    std::uint64_t attempted = 0;
    std::uint64_t completed = 0;
    FlowDeckAsrockIoStage stage = FlowDeckAsrockIoStage::Ready;
    bool faulted = false;
    int write_result = -2;
    int read_result = -2;
    std::array<unsigned char, 65> request{};
    std::array<unsigned char, 64> response{};
};

// A reply is transport evidence only. It does not certify emitted color, persisted state, or
// completion of an entire SDK operation. The SDK boundary must correlate these exchanges.
class FlowDeckAsrockIo
{
public:
    using Write = std::function<int(const unsigned char*, std::size_t)>;
    using Read = std::function<int(unsigned char*, std::size_t, int)>;
    using Authorize = std::function<void(FlowDeckAsrockIoPhase, const FlowDeckAsrockIoSnapshot&)>;
    FlowDeckAsrockIo(Write writeFunction, Read readFunction, Authorize authorizeFunction)
        : write(std::move(writeFunction)), read(std::move(readFunction)), authorize(std::move(authorizeFunction))
    {
        if(!this->write || !this->read || !this->authorize) throw std::invalid_argument("Missing ASRock transport");
    }

    void Exchange(unsigned char (&packet)[65])
    {
        // No flash commits, LED-count changes or arbitrary commands in the owned helper.
        const auto command = packet[1];
        const auto selector = packet[3];
        const bool query = command == 0x14 || command == 0x11;
        const bool valid = packet[0] == 0 &&
            ((command == 0x14 && selector >= 1 && selector <= 3) ||
             (command == 0x11 && selector <= 7) ||
             (command == 0x15 && selector == 3) ||
             (command == 0x10 && ((selector <= 7 && packet[4] <= 0x0f) ||
                                 (selector == 0xff && (packet[4] == 0xe3 || packet[4] == 0xe4)))));
        if(!valid) throw std::invalid_argument("Unsupported ASRock helper command");
        std::lock_guard<std::mutex> lock(mutex);
        if(state.faulted) throw std::runtime_error("ASRock transport requires recovery");
        if(state.attempted == (std::numeric_limits<std::uint64_t>::max)())
        {
            state.faulted = true;
            throw std::runtime_error("ASRock exchange counter exhausted");
        }
        auto next = state;
        ++next.attempted;
        next.stage = FlowDeckAsrockIoStage::WriteUncertain;
        next.write_result = next.read_result = -2;
        std::copy_n(packet, 65, next.request.begin());
        next.response.fill(0);
        // Denial here means no write was attempted: preserve the preceding IO receipt.
        authorize(FlowDeckAsrockIoPhase::BeforeWrite, next);
        state = next;
        try
        {
            state.write_result = write(state.request.data(), state.request.size());
            if(state.write_result != 65) throw std::runtime_error("Incomplete ASRock write");
            state.stage = FlowDeckAsrockIoStage::ReplyUncertain;
            authorize(FlowDeckAsrockIoPhase::BeforeRead, state);
            state.read_result = read(state.response.data(), state.response.size(), 700);
            if(state.read_result != 64) throw std::runtime_error("Incomplete ASRock reply");
            // hidapi strips the zero report ID. Query selectors are at payload offset 3,
            // matching the independently implemented native FlowDeck reader.
            if(query && state.response[3] != selector) throw std::runtime_error("ASRock reply selector mismatch");
            authorize(FlowDeckAsrockIoPhase::AfterRead, state);
            state.stage = FlowDeckAsrockIoStage::ReplyReceived;
            ++state.completed;
            std::copy(state.response.begin(), state.response.end(), packet);
            packet[64] = 0;
        }
        catch(...)
        {
            state.faulted = true;
            throw;
        }
    }

    FlowDeckAsrockIoSnapshot Snapshot() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return state;
    }

private:
    const Write write;
    const Read read;
    const Authorize authorize;
    mutable std::mutex mutex;
    FlowDeckAsrockIoSnapshot state;
};
