#include "fixture.h"
#include "ASRockPolychromeUSBController.h"
#include <atomic>
#include <cstring>
#include <iostream>
#include <memory>
#include <thread>

json fixtureSettings;
bool rgbConstructorFails = false;
static const char* path = "\\\\?\\hid#vid_26ce&pid_01a2#fixture";
static hid_device device;
static int opens = 0, closes = 0, reads = 0, failWriteAt = 0, failReadAt = 0;
static int writeReturn = -1, readReturn = 0, mismatchAt = 0;
static bool throwWrite = false, throwRead = false;
static unsigned char order = 4;
static std::array<unsigned char, 8> counts = {1, 0x1e, 2, 0x1e, 0x1e, 0x1e, 0x1e, 0x1e};
static std::vector<std::array<unsigned char, 65>> writes;
DetectedControllers DetectPolychromeUSBControllers(hid_device_info*, const std::string&);

static void Check(bool value, const char* reason) { if(!value) throw std::runtime_error(reason); }
template<class Action> static void Fails(Action action)
{
    bool failed = false;
    try { action(); } catch(const std::exception&) { failed = true; }
    Check(failed, "expected rejection");
}
hid_device* hid_open_path(const char*) { ++opens; return &device; }
void hid_close(hid_device* handle) { Check(handle == &device, "wrong handle closed"); ++closes; }
int hid_write(hid_device*, const unsigned char* data, std::size_t size)
{
    Check(size == 65, "all requests must include the report ID");
    std::array<unsigned char, 65> packet{};
    std::copy_n(data, 65, packet.begin()); writes.push_back(packet);
    if(static_cast<int>(writes.size()) == failWriteAt)
    {
        if(throwWrite) throw std::runtime_error("write callback failed");
        return writeReturn;
    }
    if(data[1] == 0x15 && data[3] == 3) order = data[4];
    return 65;
}
int hid_read_timeout(hid_device*, unsigned char* data, std::size_t size, int timeout)
{
    Check(size == 64 && timeout == 700, "reply must be bounded and omit the report ID");
    ++reads;
    if(reads == failReadAt)
    {
        if(throwRead) throw std::runtime_error("read callback failed");
        return readReturn;
    }
    std::fill_n(data, 64, static_cast<unsigned char>(0));
    const auto& request = writes.back(); data[3] = request[3];
    if(request[1] == 0x14)
    {
        if(request[3] == 1) data[4] = 5;
        if(request[3] == 2) std::copy(counts.begin(), counts.end(), data + 4);
        if(request[3] == 3) data[4] = order;
    }
    if(request[1] == 0x11)
    {
        data[4] = 1; data[5] = 12; data[6] = 34; data[7] = 56; data[8] = 224;
    }
    if(reads == mismatchAt) data[3] = 7;
    return 64;
}

static void Settings()
{
    fixtureSettings = {{"FlowDeck", {{"version", 1}, {"hid_paths", json::array({path})}}}};
    fixtureSettings["RGSwap"] = json::object();
    for(int i = 0; i < 8; ++i) fixtureSettings["RGSwap"][polychrome_USB_zone_names[i]] = false;
}
static std::unique_ptr<PolychromeUSBController> Open()
{
    std::unique_ptr<hid_device, decltype(&hid_close)> handle(hid_open_path(path), hid_close);
    auto controller = std::make_unique<PolychromeUSBController>(handle.get(), path);
    handle.release(); return controller;
}

#if FLOWDECK_PATCH >= 5
#include "authorization.inc"
#endif

static void Run(const std::string& name)
{
    Settings();
#if FLOWDECK_PATCH >= 5
    if(name.rfind("auth-", 0) == 0) { RunAuthorization(name); return; }
    FlowDeckAsrockAuthorization::Install([](const FlowDeckAsrockAuthorizationRequest&) { });
#endif
    if(name.rfind("rejected-", 0) == 0)
    {
        FlowDeckAsrockIo io([](const unsigned char*, std::size_t) -> int { throw std::runtime_error("unexpected write"); },
                           [](unsigned char*, std::size_t, int) -> int { throw std::runtime_error("unexpected read"); }
#if FLOWDECK_PATCH >= 5
                           , FlowDeckAsrockAuthorization::ForPath(path)
#endif
                           );
        unsigned char request[65]{}; request[1] = 0x14; request[3] = 1;
        if(name == "rejected-flash") request[1] = 0x12;
        if(name == "rejected-led-count") { request[1] = 0x15; request[3] = 2; }
        if(name == "rejected-query-selector") request[3] = 4;
        if(name == "rejected-report-id") request[0] = 1;
        if(name == "rejected-mode") { request[1] = 0x10; request[4] = 16; }
        if(name == "rejected-direct-marker") { request[1] = 0x10; request[3] = 255; request[4] = 0xe2; }
        Fails([&] { io.Exchange(request); });
        auto snapshot = io.Snapshot();
        Check(snapshot.attempted == 0 && !snapshot.faulted, "rejected command entered the transport"); return;
    }
    if(name == "concurrent-exchanges")
    {
        FlowDeckAsrockIo io([](const unsigned char* data, std::size_t size) { return hid_write(&device, data, size); },
                           [](unsigned char* data, std::size_t size, int timeout) { return hid_read_timeout(&device, data, size, timeout); }
#if FLOWDECK_PATCH >= 5
                           , FlowDeckAsrockAuthorization::ForPath(path)
#endif
                           );
        std::atomic<int> failures = 0;
        std::vector<std::thread> threads;
        for(int i = 0; i < 32; ++i) threads.emplace_back([&, i]
        {
            try
            {
                unsigned char request[65]{}; request[1] = 0x11; request[3] = static_cast<unsigned char>(i % 8);
                io.Exchange(request);
                if(request[3] != i % 8) ++failures;
            }
            catch(...) { ++failures; }
        });
        for(auto& thread : threads) thread.join();
        auto snapshot = io.Snapshot();
        Check(failures == 0 && snapshot.attempted == 32 && snapshot.completed == 32 && !snapshot.faulted,
              "concurrent exchanges crossed replies or counters"); return;
    }
    if(name == "construction-and-query")
    {
        auto controller = Open();
        Check(controller->GetChannelCount() == 2 && writes.size() == 4 && reads == 4, "native detection sequence changed");
        auto first = controller->GetZoneConfig(0);
        Check(first.zone == 0 && first.mode == 1 && first.color == ToRGBColor(34,12,56), "reply offset/color decode changed");
        auto state = controller->FlowDeckIoState();
        Check(state.attempted == 5 && state.completed == 5 && !state.faulted, "successful exchange evidence missing");
        controller.reset(); Check(closes == 1, "native destructor did not close HID"); return;
    }
    if(name.rfind("detection-failure-", 0) == 0 || name == "rgb-constructor-failure")
    {
        if(name == "rgb-constructor-failure") rgbConstructorFails = true;
        else failReadAt = std::stoi(name.substr(18));
        hid_device_info info = {const_cast<char*>(path), 0x26ce, 0x01a2};
        Fails([&] { DetectPolychromeUSBControllers(&info, "fixture"); });
        Check(opens == 1 && closes == 1, "failed detection leaked or double-closed HID");
        Check(DetectPolychromeUSBControllers(&info, "fixture").empty() && opens == 1, "failed admission reopened a path"); return;
    }
    if(name == "normal-detection-ownership")
    {
        hid_device_info info = {const_cast<char*>(path), 0x26ce, 0x01a2};
        auto result = DetectPolychromeUSBControllers(&info, "fixture");
        Check(result.size() == 1 && opens == 1 && closes == 0, "ownership transferred too early");
        delete result[0]; Check(closes == 1, "RGB destruction did not close HID"); return;
    }
    if(name == "partial-direct-frame") counts[2] = 100;
    auto controller = Open();
    const auto initial = controller->FlowDeckIoState();
    if(name == "volatile-zone-and-order")
    {
        controller->WriteZone(1, 2, 123, ToRGBColor(5,6,7), false);
        Check(writes.back()[3] == 2 && writes.back()[4] == 2 && writes.back()[8] == 123, "wrong physical zone packet");
        controller->SetRGSwap(false);
        Check(writes.back()[1] == 0x15 && writes.back()[3] == 3 && order == 0, "wrong volatile header"); return;
    }
    if(name == "led-count-denied")
    {
        auto before = writes.size(); Fails([&] { controller->SetZoneSize(0, 10); });
        Check(writes.size() == before && !controller->FlowDeckIoState().faulted, "unsupported command reached HID"); return;
    }
    if(name.rfind("write-", 0) == 0)
    {
        failWriteAt = static_cast<int>(writes.size()) + 1;
        if(name == "write-exception") throwWrite = true;
        else writeReturn = std::stoi(name.substr(6));
    }
    else if(name.rfind("read-", 0) == 0)
    {
        failReadAt = reads + 1;
        if(name == "read-exception") throwRead = true;
        else readReturn = std::stoi(name.substr(5));
    }
    else if(name == "query-selector-mismatch") mismatchAt = reads + 1;
    else if(name == "partial-direct-frame")
    {
        failReadAt = reads + 3;
        std::vector<RGBColor> colors(100, ToRGBColor(1,2,3));
        std::vector<zone> zones = {{1,1,colors.data()},{100,100,colors.data()}};
        Fails([&] { controller->WriteAllZones({},zones); });
        auto state = controller->FlowDeckIoState();
        Check(state.attempted == initial.attempted + 3 && state.completed == initial.completed + 2 && state.faulted,
              "partial frame prefix evidence lost");
        Check(state.request[4] == 0xe4 && state.stage == FlowDeckAsrockIoStage::ReplyUncertain, "partial chunk not recorded");
        auto count = writes.size(); Fails([&] { controller->SetRGSwap(true); });
        Check(writes.size() == count, "faulted frame allowed later writes"); return;
    }
    else throw std::runtime_error("unknown case");
    Fails([&] { controller->GetZoneConfig(0); });
    auto state = controller->FlowDeckIoState();
    Check(state.faulted && state.attempted == initial.attempted + 1 && state.completed == initial.completed, "uncertain IO counted as complete");
    Check(state.stage == (failWriteAt ? FlowDeckAsrockIoStage::WriteUncertain : FlowDeckAsrockIoStage::ReplyUncertain), "failure phase lost");
    Check(state.write_result == (failWriteAt ? (throwWrite ? -2 : writeReturn) : 65), "write return code lost");
    Check(state.read_result == (failWriteAt || throwRead ? -2 : mismatchAt ? 64 : readReturn), "read return code lost");
    const auto count = writes.size(); const int readCount = reads;
    Fails([&] { controller->WriteZone(0, 1, 224, 0, false); });
    Check(writes.size() == count && reads == readCount, "faulted transport was reused");
}

int main(int argc, char** argv)
{
    try
    {
        Check(argc == 2, "expected a case name"); Run(argv[1]);
        Check(opens == closes, "test retained a HID handle");
        std::cout << "PASS " << argv[1] << '\n'; return 0;
    }
    catch(const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
