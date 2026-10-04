// Real Windows pipe client and native IO/authorization boundary; only HID is mocked.
#include "FlowDeckAsrockBootstrap.h"
#include <iostream>
#include <memory>
#include <fstream>

static int BootstrapFixture(bool paused)
{
    int result = 21, writes = 0, reads = 0;
    try
    {
        auto channel = FlowDeckAsrockBootstrap::InstallFromEnvironment();
        if(paused)
        {
            unsigned int attempt=0;
            while(GetFileAttributesW(L"bootstrap-begin")==INVALID_FILE_ATTRIBUTES)
            { if(++attempt > 1000) throw std::runtime_error("Fixture was not released"); Sleep(10); }
        }
        constexpr const char* path="\\\\?\\hid#vid_26ce&pid_01a2#pipe-fixture";
        std::array<unsigned char,65> last{};
        FlowDeckAsrockIo io([&](const unsigned char* bytes,std::size_t)
            { ++writes; std::copy_n(bytes,65,last.begin()); return 65; },
            [&](unsigned char* bytes,std::size_t,int)
            { ++reads; std::fill_n(bytes,64,static_cast<unsigned char>(0)); bytes[3]=last[3]; return 64; },
            FlowDeckAsrockAuthorization::ForPath(path));
        result = 20;
        FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeOpen,path);
        unsigned char query[65]{}; query[1]=0x14; query[3]=1; io.Exchange(query);
        result = 0;
    }
    catch(const std::exception&) { }
    wchar_t metadata[2]{};
    const bool cleared = GetEnvironmentVariableW(FlowDeckAsrockBootstrap::EnvironmentName,metadata,2) == 0;
    const nlohmann::json value={{"code",result},{"writes",writes},{"reads",reads},{"environmentCleared",cleared}};
    { std::ofstream output("bootstrap-result.json",std::ios::binary); output << value.dump(); }
    // Fixture-only rendezvous keeps the owned child alive while .NET inspects startup state.
    // The product entry point exits immediately on any failed bootstrap instead.
    for(unsigned int i=0;i<1000 && GetFileAttributesW(L"bootstrap-finish")==INVALID_FILE_ATTRIBUTES;++i) Sleep(10);
    return result;
}

static int Raw(const std::wstring& name, const std::wstring& mode)
{
    const auto path=L"\\\\.\\pipe\\"+name;
    HANDLE pipe=CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
    if(pipe==INVALID_HANDLE_VALUE) return 92;
    auto send=[&](std::string text,std::uint32_t override_size=0)
    {
        const auto length=override_size ? override_size : static_cast<std::uint32_t>(text.size());
        unsigned char header[4]; for(unsigned int i=0;i<4;++i) header[i]=static_cast<unsigned char>(length>>(i*8)); DWORD count=0;
        WriteFile(pipe,header,mode==L"raw-partial" ? 1 : 4,&count,nullptr);
        if(mode!=L"raw-partial" && !override_size) WriteFile(pipe,text.data(),static_cast<DWORD>(text.size()),&count,nullptr);
        if(!ReadFile(pipe,header,4,&count,nullptr) || count!=4) return false;
        const auto size=static_cast<std::uint32_t>(header[0])|(static_cast<std::uint32_t>(header[1])<<8)|
                        (static_cast<std::uint32_t>(header[2])<<16)|(static_cast<std::uint32_t>(header[3])<<24);
        if(size>128) return false;
        std::string reply(size,'\0'); return ReadFile(pipe,reply.data(),size,&count,nullptr) && count==size;
    };
    FlowDeckAsrockAuthorizationRequest request{FlowDeckAsrockAuthorizationPhase::BeforeOpen,"fixture",{}, {}};
    auto text=FlowDeckAsrockWire::Request(mode==L"raw-sequence" ? 2 : 1,request);
    if(mode==L"raw-duplicate") text.insert(1,"\"version\":1,");
    if(mode==L"raw-replay" && !send(text)) { CloseHandle(pipe); return 93; }
    const auto accepted=send(text,mode==L"raw-large" ? 16385u : mode==L"raw-zero" ? 1u : 0u);
    CloseHandle(pipe); std::cout << "RAW accepted=" << accepted << '\n'; return accepted ? 94 : 22;
}

int wmain(int argc, wchar_t** argv)
{
    if(argc == 2 && std::wstring(argv[1]) == L"--bootstrap") return BootstrapFixture(false);
    if(argc == 2 && std::wstring(argv[1]) == L"--bootstrap-paused") return BootstrapFixture(true);
    if(argc != 6) return 90;
    const std::wstring name = argv[1], mode = argv[4];
    auto parent_id = static_cast<DWORD>(std::stoul(argv[2])); auto created = std::stoull(argv[3]);
    const auto timeout = static_cast<DWORD>(std::stoul(argv[5]));
    int writes = 0, reads = 0;
    if(mode.rfind(L"raw-",0)==0) return Raw(name,mode);
    try
    {
        if(mode == L"self-parent")
        {
            FILETIME c{},e{},k{},u{}; GetProcessTimes(GetCurrentProcess(),&c,&e,&k,&u);
            parent_id=GetCurrentProcessId(); created=(static_cast<std::uint64_t>(c.dwHighDateTime)<<32)|c.dwLowDateTime;
        }
        auto channel=std::make_shared<FlowDeckAsrockPipe>(name,parent_id,created,timeout);
        FlowDeckAsrockAuthorization::Install([channel](const FlowDeckAsrockAuthorizationRequest& r) { channel->Check(r); });
        constexpr const char* path="\\\\?\\hid#vid_26ce&pid_01a2#pipe-fixture";
        std::array<unsigned char,65> last{};
        FlowDeckAsrockIo io([&](const unsigned char* bytes,std::size_t size)
            { if(size != 65) throw std::runtime_error("wrong mock request"); ++writes; std::copy_n(bytes,65,last.begin()); return 65; },
            [&](unsigned char* bytes,std::size_t size,int wait)
            { if(size != 64 || wait != 700) throw std::runtime_error("wrong mock read"); ++reads; std::fill_n(bytes,64,static_cast<unsigned char>(0)); bytes[3]=last[3]; return 64; },
            FlowDeckAsrockAuthorization::ForPath(path));
        try
        {
            FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeOpen,path);
            unsigned char query[65]{}; query[1]=0x14; query[3]=1; io.Exchange(query);
            FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeSerial,path,io.Snapshot());
            {
                FlowDeckAsrockOriginScope origin({true,6,1050,42,6742,51000});
                unsigned char packet[65]{}; packet[1]=0x10; packet[4]=1; io.Exchange(packet);
            }
            std::cout << "GRANTED writes=" << writes << " reads=" << reads << '\n'; return 0;
        }
        catch(const std::exception& error)
        {
            bool reused = true;
            try { FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeOpen,path); } catch(...) { reused=false; }
            std::cout << "DENIED writes=" << writes << " reads=" << reads << " reused=" << reused << " faulted=" << channel->Faulted() << ' ' << error.what() << '\n';
            return reused ? 91 : 20;
        }
    }
    catch(const std::exception& error) { std::cout << "CONNECT_DENIED " << error.what() << '\n'; return 21; }
}
