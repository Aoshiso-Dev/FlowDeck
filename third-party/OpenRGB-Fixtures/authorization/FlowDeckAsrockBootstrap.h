/* Early bootstrap for the FlowDeck-owned Windows helper.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#include "FlowDeckAsrockPipe.h"
#include <memory>

class FlowDeckAsrockBootstrap
{
public:
    static constexpr const wchar_t* EnvironmentName = L"FLOWDECK_ASROCK_AUTH_V1";
    static constexpr DWORD MaximumLength = 1024;
    struct Metadata
    {
        std::wstring pipe;
        DWORD parent;
        std::uint64_t created;
        DWORD timeout;
    };
    static Metadata Parse(const std::string& text)
    {
        if(text.size() < 2 || text.size() > MaximumLength) throw std::runtime_error("Invalid bootstrap size");
        for(const unsigned char character : text)
            if(character < 0x20 || character > 0x7e) throw std::runtime_error("Invalid bootstrap encoding");
        std::set<std::string> keys; bool duplicate = false;
        auto value = nlohmann::json::parse(text, [&](int depth, nlohmann::json::parse_event_t event, nlohmann::json& parsed)
        {
            if(depth > 1) throw std::runtime_error("Nested bootstrap metadata");
            if(event == nlohmann::json::parse_event_t::key && !keys.insert(parsed.get<std::string>()).second) duplicate = true;
            return true;
        });
        if(duplicate || !value.is_object() || value.size() != 5 || !value.contains("version") || !value.contains("pipe") ||
           !value.contains("parent") || !value.contains("created") || !value.contains("timeoutMs") ||
           !value["version"].is_number_unsigned() || value["version"] != 1 || !value["pipe"].is_string() ||
           !value["parent"].is_number_unsigned() || value["parent"] == 0 || value["parent"] > (std::numeric_limits<DWORD>::max)() ||
           !value["created"].is_number_unsigned() || value["created"] == 0 || !value["timeoutMs"].is_number_unsigned() ||
           value["timeoutMs"] < 100 || value["timeoutMs"] > 5000)
            throw std::runtime_error("Invalid bootstrap fields");
        const auto name = value["pipe"].get<std::string>();
        const std::string prefix = "FlowDeck.OpenRgb.Authorization.";
        if(name.size() != prefix.size()+32 || name.compare(0,prefix.size(),prefix) != 0 ||
           name.find_first_not_of("0123456789abcdef",prefix.size()) != std::string::npos)
            throw std::runtime_error("Invalid bootstrap channel name");
        return {std::wstring(name.begin(),name.end()), value["parent"].get<DWORD>(),
                value["created"].get<std::uint64_t>(), value["timeoutMs"].get<DWORD>()};
    }
    static std::shared_ptr<FlowDeckAsrockPipe> InstallFromEnvironment()
    {
        // Read once and erase even invalid data before any child process or hardware detector.
        // This value is a locator, not a bearer secret; the pipe checks the actual parent process.
        wchar_t buffer[MaximumLength+1]{};
        const DWORD length = GetEnvironmentVariableW(EnvironmentName,buffer,MaximumLength+1);
        if(!SetEnvironmentVariableW(EnvironmentName,nullptr)) throw std::runtime_error("Cannot clear bootstrap metadata");
        if(length < 2 || length > MaximumLength) throw std::runtime_error("Missing or excessive bootstrap metadata");
        std::string text; text.reserve(length);
        for(DWORD i=0;i<length;++i)
        {
            if(buffer[i] < 0x20 || buffer[i] > 0x7e) throw std::runtime_error("Invalid bootstrap encoding");
            text.push_back(static_cast<char>(buffer[i]));
        }
        const auto metadata = Parse(text);
        if(metadata.parent == GetCurrentProcessId()) throw std::runtime_error("Bootstrap cannot authorize itself");
        auto channel = std::make_shared<FlowDeckAsrockPipe>(metadata.pipe,metadata.parent,metadata.created,metadata.timeout);
        FlowDeckAsrockAuthorization::Install([channel](const FlowDeckAsrockAuthorizationRequest& request) { channel->Check(request); });
        return channel;
    }
};
