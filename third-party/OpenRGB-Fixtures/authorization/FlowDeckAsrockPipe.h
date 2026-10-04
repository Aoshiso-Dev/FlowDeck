/* Windows named-pipe client for the exact owning FlowDeck parent process.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include "FlowDeckAsrockWire.h"
#include <atomic>
#include <chrono>

class FlowDeckAsrockPipe
{
    class Handle
    {
    public:
        explicit Handle(HANDLE handle_value = INVALID_HANDLE_VALUE) : value(handle_value) { }
        ~Handle() { Close(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HANDLE Get() const { return value; }
        bool Valid() const { return value != nullptr && value != INVALID_HANDLE_VALUE; }
        void Set(HANDLE next) { Close(); value = next; }
        void Close() { if(Valid()) CloseHandle(value); value = INVALID_HANDLE_VALUE; }
    private:
        HANDLE value;
    };
    using Clock = std::chrono::steady_clock;
public:
    FlowDeckAsrockPipe(const std::wstring& name, DWORD parent_id, std::uint64_t created_file_time, DWORD timeout_ms = 1000)
        : expected_parent(parent_id), timeout(timeout_ms)
    {
        const std::wstring prefix = L"FlowDeck.OpenRgb.Authorization.";
        if(name.size() != prefix.size()+32 || name.compare(0,prefix.size(),prefix) != 0 ||
           name.find_first_not_of(L"0123456789abcdef",prefix.size()) != std::wstring::npos ||
           parent_id == 0 || created_file_time == 0 || timeout_ms < 100 || timeout_ms > 5000)
            throw std::invalid_argument("Invalid authorization channel identity");
        parent.Set(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,FALSE,parent_id));
        FILETIME created{}, exited{}, kernel{}, user{};
        if(!parent.Valid() || !GetProcessTimes(parent.Get(),&created,&exited,&kernel,&user) || GetProcessId(parent.Get()) != parent_id ||
           ((static_cast<std::uint64_t>(created.dwHighDateTime)<<32) | created.dwLowDateTime) != created_file_time)
            throw std::runtime_error("Authorization parent identity mismatch");
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms);
        const auto path = L"\\\\.\\pipe\\" + name;
        while(true)
        {
            CheckParent();
            pipe.Set(CreateFileW(path.c_str(),GENERIC_READ | GENERIC_WRITE,0,nullptr,OPEN_EXISTING,
                                 FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,nullptr));
            if(pipe.Valid()) break;
            const auto error = GetLastError();
            if(error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) throw std::runtime_error("Cannot open authorization channel");
            const auto remaining = Remaining(deadline);
            if(WaitForSingleObject(parent.Get(),(std::min)(remaining,DWORD(10))) != WAIT_TIMEOUT)
                throw std::runtime_error("Authorization parent exited while connecting");
        }
        CheckPeer(); (void)Remaining(deadline);
    }
    FlowDeckAsrockPipe(const FlowDeckAsrockPipe&) = delete;
    FlowDeckAsrockPipe& operator=(const FlowDeckAsrockPipe&) = delete;

    void Check(const FlowDeckAsrockAuthorizationRequest& request)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeout);
        std::unique_lock<std::timed_mutex> lock(mutex,std::defer_lock);
        if(!lock.try_lock_until(deadline)) { failed = true; throw std::runtime_error("Authorization queue expired"); }
        try
        {
            CheckPeer(); (void)Remaining(deadline);
            if(sequence >= (std::numeric_limits<std::uint64_t>::max)()-1) throw std::runtime_error("Authorization sequence exhausted");
            const auto text = FlowDeckAsrockWire::Request(++sequence,request);
            unsigned char length[4]; Encode(length,static_cast<std::uint32_t>(text.size()));
            Transfer(length,4,true,deadline); Transfer(reinterpret_cast<unsigned char*>(const_cast<char*>(text.data())),text.size(),true,deadline);
            Transfer(length,4,false,deadline); const auto count = Decode(length);
            if(count < 2 || count > FlowDeckAsrockWire::MaximumReply) throw std::runtime_error("Invalid authorization frame size");
            std::string reply(count,'\0'); Transfer(reinterpret_cast<unsigned char*>(reply.data()),count,false,deadline);
            CheckPeer(); (void)Remaining(deadline);
            if(!FlowDeckAsrockWire::Reply(sequence,reply)) throw std::runtime_error("Parent denied ASRock hardware access");
            // Parsing or another queued request may also have exhausted/revoked this permission.
            CheckPeer(); (void)Remaining(deadline);
        }
        catch(...) { failed = true; pipe.Close(); throw; }
    }
    bool Faulted() const { return failed; }
private:
    static void Encode(unsigned char (&bytes)[4], std::uint32_t value)
    { for(unsigned int i=0;i<4;++i) bytes[i]=static_cast<unsigned char>(value>>(i*8)); }
    static std::uint32_t Decode(const unsigned char (&bytes)[4])
    { return bytes[0] | (static_cast<std::uint32_t>(bytes[1])<<8) | (static_cast<std::uint32_t>(bytes[2])<<16) | (static_cast<std::uint32_t>(bytes[3])<<24); }
    static DWORD Remaining(Clock::time_point deadline)
    {
        const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline-Clock::now()).count();
        if(remaining <= 0) throw std::runtime_error("Authorization deadline expired");
        return static_cast<DWORD>(remaining);
    }
    void CheckParent() const
    {
        if(failed || !parent.Valid() || WaitForSingleObject(parent.Get(),0) != WAIT_TIMEOUT)
            throw std::runtime_error("Authorization parent is unavailable");
    }
    void CheckPeer() const
    {
        CheckParent(); ULONG process_id = 0;
        if(!pipe.Valid() || !GetNamedPipeServerProcessId(pipe.Get(),&process_id) || process_id != expected_parent)
            throw std::runtime_error("Authorization pipe server mismatch");
    }
    void Transfer(unsigned char* data, std::size_t size, bool writing, Clock::time_point deadline)
    {
        for(std::size_t offset=0;offset<size;)
        {
            CheckPeer(); (void)Remaining(deadline);
            Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));
            if(!event.Valid()) throw std::runtime_error("Cannot create authorization IO event");
            OVERLAPPED operation{}; operation.hEvent=event.Get(); DWORD count=0;
            const auto requested=static_cast<DWORD>(size-offset);
            const auto complete = writing ? WriteFile(pipe.Get(),data+offset,requested,&count,&operation)
                                          : ReadFile(pipe.Get(),data+offset,requested,&count,&operation);
            if(!complete)
            {
                if(GetLastError() != ERROR_IO_PENDING) throw std::runtime_error("Authorization pipe IO failed");
                DWORD wait;
                try
                {
                    HANDLE handles[2]={event.Get(),parent.Get()};
                    wait=WaitForMultipleObjects(2,handles,FALSE,Remaining(deadline));
                }
                catch(...) { CancelIoEx(pipe.Get(),&operation); GetOverlappedResult(pipe.Get(),&operation,&count,TRUE); throw; }
                if(wait != WAIT_OBJECT_0)
                {
                    // Keep OVERLAPPED and its buffer alive until Windows completes cancellation.
                    CancelIoEx(pipe.Get(),&operation); GetOverlappedResult(pipe.Get(),&operation,&count,TRUE);
                    throw std::runtime_error("Authorization IO expired or parent exited");
                }
                if(!GetOverlappedResult(pipe.Get(),&operation,&count,FALSE)) throw std::runtime_error("Authorization pipe IO completion failed");
            }
            if(count == 0 || count > requested) throw std::runtime_error("Authorization pipe closed");
            offset+=count; (void)Remaining(deadline);
        }
    }
    const DWORD expected_parent, timeout;
    Handle parent, pipe;
    std::atomic<bool> failed{false};
    std::uint64_t sequence=0;
    std::timed_mutex mutex;
};
