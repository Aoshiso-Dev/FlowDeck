/* Bounded wire format shared with FlowDeck's parent authorization server.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#include "FlowDeckAsrockAuthorization.h"
#include <nlohmann/json.hpp>
#include <set>

class FlowDeckAsrockWire
{
public:
    static constexpr std::size_t MaximumRequest = 16384;
    static constexpr std::size_t MaximumReply = 128;
    static std::string Request(std::uint64_t sequence, const FlowDeckAsrockAuthorizationRequest& request)
    {
        const auto& source = request.origin; const auto& io = request.io;
        nlohmann::json value = {{"version",1},{"sequence",sequence},{"phase",static_cast<int>(request.phase)},{"path",request.path},
            {"origin",{{"sdk",source.sdk},{"protocol",source.protocol},{"command",source.command},{"controller",source.controller},
                       {"serverPort",source.server_port},{"peerPort",source.peer_port}}},
            {"io",{{"attempted",io.attempted},{"completed",io.completed},{"stage",static_cast<int>(io.stage)},{"faulted",io.faulted},
                   {"writeResult",io.write_result},{"readResult",io.read_result},{"request",io.request},{"response",io.response}}}};
        auto text = value.dump();
        if(sequence == 0 || sequence == (std::numeric_limits<std::uint64_t>::max)() || text.size() > MaximumRequest)
            throw std::runtime_error("ASRock authorization request is too large");
        return text;
    }
    static bool Reply(std::uint64_t sequence, const std::string& text)
    {
        if(text.size() < 2 || text.size() > MaximumReply) throw std::runtime_error("Invalid authorization reply size");
        std::set<std::string> keys; bool duplicate = false;
        auto value = nlohmann::json::parse(text, [&](int depth, nlohmann::json::parse_event_t event, nlohmann::json& parsed)
        {
            if(depth > 1) throw std::runtime_error("Nested authorization reply");
            if(event == nlohmann::json::parse_event_t::key && !keys.insert(parsed.get<std::string>()).second) duplicate = true;
            return true;
        });
        if(duplicate || !value.is_object() || value.size() != 3 || !value.contains("version") || !value.contains("sequence") || !value.contains("allow") ||
           !value["version"].is_number_integer() || value["version"] != 1 || !value["sequence"].is_number_unsigned() ||
           value["sequence"].get<std::uint64_t>() != sequence || !value["allow"].is_boolean())
            throw std::runtime_error("Uncorrelated authorization reply");
        return value["allow"].get<bool>();
    }
};
