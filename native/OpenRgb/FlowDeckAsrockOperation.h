/* Synchronous operation receipts for the pinned FlowDeck OpenRGB helper.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#include "FlowDeckAsrockIo.h"
#include <nlohmann/json.hpp>

enum class FlowDeckOperationStage { Idle, Running, Completed, Failed };
struct FlowDeckOperationSnapshot
{
    std::uint64_t sequence = 0;
    unsigned int command = 0;
    FlowDeckOperationStage stage = FlowDeckOperationStage::Idle;
    FlowDeckAsrockIoSnapshot before, after;
};

// Serializes whole device operations, not just individual HID exchanges. A failed operation
// retains its last receipt and disables later operations until ownership is released.
class FlowDeckAsrockOperation
{
public:
    template<class Observe, class Apply> bool Run(unsigned int command, Observe observe, Apply apply)
    {
        std::lock_guard<std::mutex> operation_lock(operation_mutex);
        auto current = Snapshot();
        if(current.stage == FlowDeckOperationStage::Failed) return false;
        if(current.sequence == (std::numeric_limits<std::uint64_t>::max)())
        {
            current.stage = FlowDeckOperationStage::Failed; Store(current); return false;
        }
        ++current.sequence;
        current.command = command;
        current.stage = FlowDeckOperationStage::Running;
        Store(current);
        try
        {
            current.before = current.after = observe();
            Store(current);
            if(current.before.faulted || current.before.attempted != current.before.completed)
                throw std::runtime_error("ASRock transport was not idle");
            apply();
            current.after = observe();
            if(current.after.faulted || current.after.attempted != current.after.completed ||
               current.after.attempted < current.before.attempted)
                throw std::runtime_error("ASRock operation did not complete");
            current.stage = FlowDeckOperationStage::Completed;
            Store(current); return true;
        }
        catch(...)
        {
            try { current.after = observe(); } catch(...) { }
            current.stage = FlowDeckOperationStage::Failed;
            Store(current); return false;
        }
    }

    FlowDeckOperationSnapshot Snapshot() const
    {
        std::lock_guard<std::mutex> lock(state_mutex); return state;
    }
    bool Succeeded() const { return Snapshot().stage == FlowDeckOperationStage::Completed; }
    nlohmann::json Json() const
    {
        const auto value = Snapshot();
        auto io = [](const FlowDeckAsrockIoSnapshot& v) -> nlohmann::json
        {
            return {{"attempted", v.attempted}, {"completed", v.completed}, {"stage", static_cast<int>(v.stage)},
                    {"faulted", v.faulted}, {"writeResult", v.write_result}, {"readResult", v.read_result},
                    {"request", v.request}, {"response", v.response}};
        };
        return {{"version", 1}, {"sequence", value.sequence}, {"command", value.command},
                {"stage", static_cast<int>(value.stage)}, {"before", io(value.before)}, {"after", io(value.after)}};
    }
private:
    void Store(const FlowDeckOperationSnapshot& value)
    {
        std::lock_guard<std::mutex> lock(state_mutex); state = value;
    }
    std::mutex operation_mutex;
    mutable std::mutex state_mutex;
    FlowDeckOperationSnapshot state;
};
