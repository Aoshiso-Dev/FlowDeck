#include "FlowDeckAsrockAuthorization.h"

static void RunAuthorization(const std::string& name)
{
    using Phase = FlowDeckAsrockAuthorizationPhase;
    using Request = FlowDeckAsrockAuthorizationRequest;
    hid_device_info info = {const_cast<char*>(path), 0x26ce, 0x01a2};
    if(name == "auth-missing")
    {
        Fails([&] { DetectPolychromeUSBControllers(&info, "fixture"); });
        Check(opens == 0 && writes.empty() && reads == 0, "missing channel reached HID");
        Fails([] { FlowDeckAsrockAuthorization::Install([](const Request&) { }); }); return;
    }
    if(name == "auth-empty-io-check")
    {
        Fails([] { FlowDeckAsrockIo io([](const unsigned char*, std::size_t) { return 65; },
                                     [](unsigned char*, std::size_t, int) { return 64; }, {}); }); return;
    }
    std::vector<Request> requests; std::mutex request_mutex;
    Phase denied = Phase::BeforeOpen; std::uint64_t denied_attempt = 0; bool deny = false;
    FlowDeckAsrockAuthorization::Install([&](const Request& r)
    {
        std::lock_guard<std::mutex> lock(request_mutex); requests.push_back(r);
        Check(r.path == path, "wrong captured physical path");
        if(deny && r.phase == denied && r.io.attempted == denied_attempt) throw std::runtime_error("authorization refused");
    });
    if(name == "auth-replace-denied")
    { Fails([] { FlowDeckAsrockAuthorization::Install([](const Request&) { }); }); return; }
    if(name == "auth-invalid-path")
    {
        for(const auto& value : {std::string(), std::string("has space"), std::string("x\0y",3), std::string(2049,'a'), std::string("\x7f")})
            Fails([&] { FlowDeckAsrockAuthorization::Require(Phase::BeforeOpen, value); });
        Fails([] { FlowDeckAsrockAuthorization::ForPath(nullptr); });
        Check(requests.empty(), "malformed path reached channel"); return;
    }
    if(name == "auth-captured-path")
    {
        std::string changing = path; const auto check = FlowDeckAsrockAuthorization::ForPath(changing.c_str());
        changing[0] = 'x'; check(FlowDeckAsrockIoPhase::BeforeWrite, {});
        Check(requests.size() == 1 && requests[0].path == path, "path borrowed mutable storage"); return;
    }
    if(name == "auth-origin-scope" || name == "auth-origin-threads")
    {
        auto run = [&](unsigned short port)
        {
            Check(!FlowDeckAsrockOriginScope::Current().sdk, "thread inherited SDK context");
            const FlowDeckAsrockOriginScope first({true,6,1050,42,6742,port});
            {
                const FlowDeckAsrockOriginScope second({true,6,1101,43,6743,static_cast<unsigned short>(port+1)});
                FlowDeckAsrockAuthorization::Require(Phase::BeforeSerial,path);
                Check(FlowDeckAsrockOriginScope::Current().command == 1101, "nested origin lost");
            }
            Check(FlowDeckAsrockOriginScope::Current().peer_port == port, "origin not restored");
            FlowDeckAsrockAuthorization::Require(Phase::BeforeSerial,path);
        };
        if(name == "auth-origin-scope") run(51000);
        else
        {
            std::vector<std::thread> threads;
            for(unsigned short i = 0; i < 16; ++i) threads.emplace_back(run,static_cast<unsigned short>(51000+i*2));
            for(auto& thread : threads) thread.join();
        }
        Check(!FlowDeckAsrockOriginScope::Current().sdk, "origin leaked after scope");
        Check(requests.size() == (name == "auth-origin-scope" ? 2u : 32u), "origin checks missing");
        for(const auto& r : requests)
            Check(r.origin.sdk && r.origin.protocol == 6 && r.origin.peer_port >= 51000 &&
                  r.origin.controller == (r.origin.command == 1101 ? 43u : 42u), "origin fields crossed"); return;
    }
    if(name == "auth-open-denied")
    {
        deny = true; Fails([&] { DetectPolychromeUSBControllers(&info,"fixture"); });
        Check(opens == 0 && writes.empty() && requests.size() == 1, "denied open touched HID");
        deny = false;
        Check(DetectPolychromeUSBControllers(&info,"fixture").empty() && requests.size() == 1, "denied path was admitted again"); return;
    }
    if(name.rfind("auth-detection-",0) == 0)
    {
        denied_attempt = static_cast<std::uint64_t>(name.back()-'0'); deny = true;
        denied = name.find("-write-") != std::string::npos ? Phase::BeforeWrite : name.find("-read-") != std::string::npos ? Phase::BeforeRead : Phase::AfterRead;
        Fails([&] { DetectPolychromeUSBControllers(&info,"fixture"); });
        Check(opens == 1 && closes == 1, "authorization failure leaked detector handle");
        Check(writes.size() == denied_attempt - (denied == Phase::BeforeWrite ? 1u : 0u), "denied stage issued a write");
        Check(reads == static_cast<int>(denied_attempt) - (denied == Phase::AfterRead ? 0 : 1), "denied stage issued a read");
        return;
    }
    auto controller = Open(); auto initial = controller->FlowDeckIoState();
    if(name == "auth-successful-trace")
    {
        Check(requests.size() == 12 && reads == 4, "per-exchange checks missing");
        for(std::size_t i = 0; i < 4; ++i)
        {
            const auto& before = requests[i*3]; const auto& middle = requests[i*3+1]; const auto& after = requests[i*3+2];
            Check(before.phase == Phase::BeforeWrite && middle.phase == Phase::BeforeRead && after.phase == Phase::AfterRead, "authorization phase order changed");
            Check(before.io.attempted == i+1 && before.io.completed == i && before.io.request == writes[i] && before.io.read_result == -2, "write proposal missing");
            Check(middle.io.write_result == 65 && middle.io.read_result == -2 && after.io.read_result == 64 && after.io.completed == i, "prematurely certified read");
            Check(after.io.response[3] == writes[i][3] && after.io.request == before.io.request, "reply evidence not correlated");
        }
        return;
    }
    if(name == "auth-serial" || name == "auth-serial-denied")
    {
        const auto count = requests.size(); denied = Phase::BeforeSerial; denied_attempt = initial.attempted; deny = name == "auth-serial-denied";
        if(deny) Fails([&] { controller->GetSerialString(); }); else controller->GetSerialString();
        Check(serialReads == (deny ? 0 : 1) && requests.size() == count+1 && requests.back().phase == Phase::BeforeSerial, "serial IO bypassed guard"); return;
    }
    Check(name.rfind("auth-operation-",0) == 0,"unknown authorization case");
    denied = name == "auth-operation-write" ? Phase::BeforeWrite : name == "auth-operation-read" ? Phase::BeforeRead : Phase::AfterRead;
    denied_attempt = initial.attempted+1; deny = true;
    Fails([&] { controller->GetZoneConfig(0); }); const auto failed = controller->FlowDeckIoState();
    if(denied == Phase::BeforeWrite)
        Check(failed.attempted == initial.attempted && failed.completed == initial.completed && !failed.faulted && failed.request == initial.request, "unattempted IO changed receipt");
    else
    {
        Check(failed.attempted == initial.attempted+1 && failed.completed == initial.completed && failed.faulted &&
              failed.stage == FlowDeckAsrockIoStage::ReplyUncertain && failed.write_result == 65 &&
              failed.read_result == (denied == Phase::BeforeRead ? -2 : 64), "denied partial IO lost uncertainty");
        deny = false; const auto count = requests.size(); Fails([&] { controller->GetZoneConfig(0); });
        Check(requests.size() == count, "uncertain IO was reused");
    }
}
