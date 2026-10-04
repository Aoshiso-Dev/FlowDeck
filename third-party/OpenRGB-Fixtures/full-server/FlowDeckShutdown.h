/* Private FlowDeck owned-child shutdown, used only after parent authentication.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <stdexcept>
#include <string>

class FlowDeckShutdown
{
public:
    static constexpr const wchar_t* EnvironmentName = L"FLOWDECK_SHUTDOWN_V1";
    FlowDeckShutdown()
    {
        wchar_t buffer[128]{};
        const DWORD count = GetEnvironmentVariableW(EnvironmentName, buffer, 128);
        if(!SetEnvironmentVariableW(EnvironmentName, nullptr)) throw std::runtime_error("Cannot clear shutdown metadata");
        const std::wstring prefix = L"Local\\FlowDeck.OpenRgb.Shutdown.";
        if(count != prefix.size()+32 || count >= 128) throw std::runtime_error("Invalid shutdown metadata size");
        const std::wstring name(buffer,count);
        if(name.compare(0,prefix.size(),prefix) != 0 || name.find_first_not_of(L"0123456789abcdef",prefix.size()) != std::wstring::npos)
            throw std::runtime_error("Invalid shutdown event name");
        event = OpenEventW(SYNCHRONIZE,FALSE,name.c_str());
        if(!event) throw std::runtime_error("Cannot open parent's shutdown event");
    }
    ~FlowDeckShutdown() { if(event) CloseHandle(event); }
    FlowDeckShutdown(const FlowDeckShutdown&) = delete;
    FlowDeckShutdown& operator=(const FlowDeckShutdown&) = delete;
    bool Wait(DWORD milliseconds) const
    {
        const DWORD result = WaitForSingleObject(event,milliseconds);
        if(result == WAIT_OBJECT_0) return true;
        if(result == WAIT_TIMEOUT) return false;
        throw std::runtime_error("Cannot wait for parent's shutdown event");
    }
    // Shared by the real headless Windows entry and the native mock-server fixture.
    template<class Server> void RunServerUntilStopped(Server* server) const
    {
        while(server->GetOnline() && !Wait(100)) { }
        server->StopServer();
    }
private:
    HANDLE event = nullptr;
};
