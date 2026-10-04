/* Mandatory authorization boundary for the owned ASRock helper.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#include "FlowDeckAsrockIo.h"
#include <memory>
#include <string>

enum class FlowDeckAsrockAuthorizationPhase { BeforeOpen, BeforeSerial, BeforeWrite, BeforeRead, AfterRead };

// Socket endpoints are observed by the SDK server, never supplied in an SDK payload.
// The parent must still compare them with its own live connection and pending operation.
struct FlowDeckAsrockOrigin
{
    bool sdk = false;
    unsigned int protocol = 0;
    unsigned int command = 0;
    unsigned int controller = 0;
    unsigned short server_port = 0;
    unsigned short peer_port = 0;
};

class FlowDeckAsrockOriginScope
{
public:
    explicit FlowDeckAsrockOriginScope(const FlowDeckAsrockOrigin& value) : previous(current) { current = value; }
    ~FlowDeckAsrockOriginScope() { current = previous; }
    FlowDeckAsrockOriginScope(const FlowDeckAsrockOriginScope&) = delete;
    FlowDeckAsrockOriginScope& operator=(const FlowDeckAsrockOriginScope&) = delete;
    static FlowDeckAsrockOrigin Current() { return current; }
private:
    const FlowDeckAsrockOrigin previous;
    inline static thread_local FlowDeckAsrockOrigin current{};
};

struct FlowDeckAsrockAuthorizationRequest
{
    FlowDeckAsrockAuthorizationPhase phase;
    std::string path;
    FlowDeckAsrockOrigin origin;
    FlowDeckAsrockIoSnapshot io;
};

class FlowDeckAsrockAuthorization
{
public:
    using Check = std::function<void(const FlowDeckAsrockAuthorizationRequest&)>;

    // Only compiled bootstrap code can install the protected parent channel. No SDK/config
    // command installs it. Sealing on first use prevents a denied startup from being revived.
    static void Install(Check check)
    {
        if(!check) throw std::invalid_argument("Missing ASRock authorization channel");
        auto value = std::make_shared<const Check>(std::move(check));
        std::lock_guard<std::mutex> lock(mutex);
        if(sealed) throw std::runtime_error("ASRock authorization already sealed");
        checker = std::move(value); sealed = true;
    }

    static void Require(FlowDeckAsrockAuthorizationPhase phase, const std::string& hid_path,
                        const FlowDeckAsrockIoSnapshot& io = {})
    {
        std::shared_ptr<const Check> check;
        {
            std::lock_guard<std::mutex> lock(mutex);
            sealed = true; check = checker;
        }
        if(!check) throw std::runtime_error("ASRock parent authorization is unavailable");
        if(hid_path.empty() || hid_path.size() > 2048) throw std::invalid_argument("Invalid ASRock authorization path");
        for(const unsigned char character : hid_path)
            if(character < 0x21 || character > 0x7e) throw std::invalid_argument("Invalid ASRock authorization path");
        (*check)({phase, hid_path, FlowDeckAsrockOriginScope::Current(), io});
    }

    static FlowDeckAsrockIo::Authorize ForPath(const char* hid_path)
    {
        if(hid_path == nullptr) throw std::invalid_argument("Missing ASRock path");
        std::size_t size = 0;
        while(size <= 2048 && hid_path[size] != '\0') ++size;
        if(size == 0 || size > 2048) throw std::invalid_argument("Invalid ASRock path");
        // Own the original path; do not consult mutable settings or SDK display names.
        return [captured = std::string(hid_path, size)](FlowDeckAsrockIoPhase phase, const FlowDeckAsrockIoSnapshot& io)
        {
            auto target = FlowDeckAsrockAuthorizationPhase::BeforeWrite;
            if(phase == FlowDeckAsrockIoPhase::BeforeRead) target = FlowDeckAsrockAuthorizationPhase::BeforeRead;
            else if(phase == FlowDeckAsrockIoPhase::AfterRead) target = FlowDeckAsrockAuthorizationPhase::AfterRead;
            Require(target, captured, io);
        };
    }
private:
    inline static std::mutex mutex;
    inline static bool sealed = false;
    inline static std::shared_ptr<const Check> checker;
};
