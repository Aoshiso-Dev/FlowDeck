#include "fixture.h"
#include "RGBController_ASRockPolychromeUSB.h"
#include <atomic>
#include <condition_variable>
#include <iostream>
#include <memory>

json fixtureSettings;
static hid_device device;
static int closes = 0, reads = 0, failReadAt = 0, blockReadAt = 0;
static unsigned char order = 4;
static std::array<unsigned char, 8> counts = {1, 0x1e, 2, 0x1e, 0x1e, 0x1e, 0x1e, 0x1e};
static std::vector<std::array<unsigned char, 65>> writes;
static std::mutex blockingMutex;
static std::condition_variable blocking;
static bool entered = false, released = false;
static void Check(bool value, const char* message) { if(!value) throw std::runtime_error(message); }
hid_device* hid_open_path(const char*) { return &device; }
void hid_close(hid_device* h) { Check(h == &device, "wrong handle"); ++closes; }
int hid_write(hid_device*, const unsigned char* data, std::size_t length)
{
    Check(length == 65, "incorrect request size");
    std::array<unsigned char, 65> p{}; std::copy_n(data, 65, p.begin()); writes.push_back(p);
    if(data[1] == 0x15 && data[3] == 3) order = data[4];
    return 65;
}
int hid_read_timeout(hid_device*, unsigned char* data, std::size_t length, int timeout)
{
    Check(length == 64 && timeout == 700, "incorrect read arguments"); ++reads;
    if(reads == blockReadAt)
    {
        std::unique_lock<std::mutex> lock(blockingMutex); entered = true; blocking.notify_all();
        blocking.wait(lock, [] { return released; });
    }
    if(reads == failReadAt) return 0;
    std::fill_n(data, 64, static_cast<unsigned char>(0));
    const auto& p = writes.back(); data[3] = p[3];
    if(p[1] == 0x14)
    {
        if(p[3] == 1) data[4] = 5;
        if(p[3] == 2) std::copy(counts.begin(), counts.end(), data + 4);
        if(p[3] == 3) data[4] = order;
    }
    if(p[1] == 0x11) { data[4] = 1; data[5] = 12; data[6] = 34; data[7] = 56; data[8] = 224; }
    return 64;
}
class TestController : public RGBController_PolychromeUSB
{
public:
    explicit TestController(PolychromeUSBController* native) : RGBController_PolychromeUSB(native), Native(native) { }
    PolychromeUSBController* Native;
    // Stage the same cached values the SDK parser sets before invoking UpdateMode/LEDs.
    void StageMode(int index, unsigned int speed = 224)
    { active_mode = index; if(index >= 0 && static_cast<std::size_t>(index) < modes.size()) modes[index].speed = speed; }
    void StageLastColor(RGBColor color) { colors.back() = color; }
};
static std::unique_ptr<TestController> Open()
{
    fixtureSettings["RGSwap"] = json::object();
    for(int i = 0; i < 8; ++i) fixtureSettings["RGSwap"][polychrome_USB_zone_names[i]] = false;
    auto native = std::make_unique<PolychromeUSBController>(&device, "fixture");
    auto rgb = std::make_unique<TestController>(native.get());
    native.release(); return rgb;
}
static json Receipt(RGBController* rgb) { return json::parse(rgb->FlowDeckOperationState()); }
#include "sdk-fixture.h"
#if FLOWDECK_PATCH >= 5
#include "authorization.inc"
#include "detection-authorization.inc"
#endif
static void Run(const std::string& name)
{
#if FLOWDECK_PATCH >= 5
    if(name.rfind("startup-",0)==0) { RunDetectionTrace(name); return; }
    if(name.rfind("auth-",0)==0) { RunSdkAuthorization(name); return; }
    FlowDeckAsrockAuthorization::Install([](const FlowDeckAsrockAuthorizationRequest&) { });
#endif
    if(name == "zero-led-first-zone" || name == "direct-empty-first" || name == "direct-empty-target") counts[0] = 0;
    if(name == "single-zone-mode") counts[2] = 0x1e;
    if(name == "partial-direct-frame") counts[2] = 100;
    auto rgb = Open(); RGBController* api = rgb.get();
    Check(api->FlowDeckManaged() && !api->FlowDeckLastOperationSucceeded(), "initial receipt not idle");
    Check(Receipt(api)["sequence"] == 0, "detection counted as SDK operation");
#if FLOWDECK_PATCH >= 4
    Check(Receipt(api)["before"] == Receipt(api)["after"] && Receipt(api)["after"]["completed"] == reads, "initial native IO not seeded");
    if(name == "initial-baseline" || name == "native-receipt-wire")
    {
        const auto initial_receipt = Receipt(api); api->UpdateMode(); const auto completed = Receipt(api);
        Check(completed["before"] == initial_receipt["after"], "first operation does not continue detection receipt");
        if(name == "native-receipt-wire")
        {
            failReadAt = reads + 1; api->UpdateLEDs(); const auto failed = Receipt(api);
            Check(failed["before"] == completed["after"] && failed["stage"] == 3, "failed export does not continue receipt");
            std::cout << "WIRE " << json::array({initial_receipt, completed, failed}).dump() << '\n';
        }
        return;
    }
    if(name == "single-zone-mode") { Check(api->GetActiveMode() == 1, "single zone native mode lost"); return; }
    if(name == "out-of-band-io")
    {
        (void)rgb->Native->GetZoneConfig(0); const auto count = writes.size(); api->UpdateMode();
        Check(Receipt(api)["stage"] == 3 && writes.size() == count, "untracked IO did not stop the next operation"); return;
    }
    if(name == "direct-effective-order" || name == "direct-empty-first" || name == "direct-zone" || name == "direct-single" || name == "restored-order" || name == "direct-empty-target")
    {
        rgb->StageMode(15); api->UpdateMode(); Check(order == 0 && api->FlowDeckLastOperationSucceeded(), "direct entry incomplete");
        Check(writes.back()[3] == 2 && writes.back()[5] == 34 && writes.back()[6] == 12, "Direct mode retained old wire order");
        if(name == "restored-order")
        {
            rgb->StageMode(2); api->UpdateMode(); Check(order == 4 && writes.back()[5] == 12 && writes.back()[6] == 34, "native order was not restored"); return;
        }
        const auto first = writes.size();
        if(name == "direct-empty-target")
        { api->UpdateZoneLEDs(0); Check(writes.size() == first, "empty zone triggered a whole-device frame"); return; }
        if(name == "direct-zone" || name == "direct-single") rgb->StageLastColor(ToRGBColor(70,80,90));
        if(name == "direct-zone") api->UpdateZoneLEDs(1);
        else if(name == "direct-single") api->UpdateSingleLED(2);
        else api->UpdateLEDs();
        Check(writes.size() > first && writes[first][4] == 0xe3, "Direct color request sent Native registers");
        const auto offset = counts[0] == 0 ? 9u : 12u;
        Check(writes[first][offset] == 34 && writes[first][offset+1] == 12, "Direct frame retained original cached color order");
        if(name == "direct-zone" || name == "direct-single") Check(writes[first][15] == 80 && writes[first][16] == 70, "partial Direct request lost the selected LED");
        Check(api->FlowDeckLastOperationSucceeded(), "Direct request did not finish"); return;
    }
#endif
    if(name.rfind("sdk-", 0) == 0) { RunSdk(api, name); return; }
    auto initial = reads;
    if(name == "synchronous-mode" || name == "zero-led-first-zone")
    {
        rgb->StageMode(2, 123); api->UpdateMode();
        auto r = Receipt(api);
        Check(api->FlowDeckLastOperationSucceeded() && r["command"] == 1101 && r["sequence"] == 1 && r["stage"] == 2, "mode receipt missing");
        Check(reads == initial + (counts[0] == 0 ? 2 : 3), "mode returned before HID completed");
        Check(writes.back()[3] == 2 && writes.back()[4] == 2 && writes.back()[8] == 123, "mode packet mismatch");
        api->UpdateLEDs(); r = Receipt(api);
        Check(r["sequence"] == 2 && r["command"] == 1050 && r["stage"] == 2, "color update coalesced");
        return;
    }
    if(name == "synchronous-zone" || name == "synchronous-single")
    {
        rgb->StageLastColor(ToRGBColor(1,2,3));
        if(name == "synchronous-zone") api->UpdateZoneLEDs(1); else api->UpdateSingleLED(2);
        const auto r = Receipt(api);
        Check(reads == initial + 1 && r["stage"] == 2 && r["command"] == (name == "synchronous-zone" ? 1051 : 1052), "zone/single completion missing");
        Check(writes.back()[3] == 2, "wrong physical zone"); return;
    }
    if(name == "running-receipt")
    {
        blockReadAt = reads + 1; std::atomic<bool> returned = false;
        std::thread worker([&] { api->UpdateMode(); returned = true; });
        { std::unique_lock<std::mutex> lock(blockingMutex); blocking.wait(lock, [] { return entered; }); }
        const auto during = Receipt(api); const bool premature = returned.load();
        { std::lock_guard<std::mutex> lock(blockingMutex); released = true; } blocking.notify_all(); worker.join();
        Check(!premature && during["stage"] == 1 && during["sequence"] == 1, "running receipt blocked or prematurely completed");
        Check(Receipt(api)["stage"] == 2 && returned, "completion not published"); return;
    }
    if(name == "serialized-operations")
    {
        std::vector<std::thread> threads;
        for(int i=0; i<16; ++i) threads.emplace_back([&] { api->UpdateMode(); });
        for(auto& thread : threads) thread.join();
        Check(Receipt(api)["sequence"] == 16 && reads == initial + 48, "operations crossed/coalesced"); return;
    }
    if(name == "partial-direct-frame")
    {
        rgb->StageMode(15); api->UpdateMode(); Check(api->FlowDeckLastOperationSucceeded(), "direct mode entry failed");
        failReadAt = reads + 3; api->UpdateLEDs();
        const auto r = Receipt(api);
        Check(r["command"] == 1050 && r["sequence"] == 2 && r["stage"] == 3, "partial frame not retained");
        Check(r["after"]["attempted"].get<int>() == r["before"]["attempted"].get<int>() + 3 &&
              r["after"]["completed"].get<int>() == r["before"]["completed"].get<int>() + 2, "partial frame prefix lost");
    }
    else if(name.rfind("failed-mode-", 0) == 0)
    {
        const int offset = std::stoi(name.substr(12)); failReadAt = reads + offset; api->UpdateMode();
        const auto r = Receipt(api);
        Check(!api->FlowDeckLastOperationSucceeded() && r["stage"] == 3 && r["sequence"] == 1, "failed mode escaped/was successful");
        Check(reads == initial + offset && r["after"]["completed"] == initial + offset - 1, "failed prefix continued or lost");
    }
    else if(name == "invalid-mode") { rgb->StageMode(99); api->UpdateMode(); }
    else if(name == "invalid-zone") api->UpdateZoneLEDs(99);
    else if(name == "invalid-led") api->UpdateSingleLED(99);
    else if(name == "resize-denied") rgb->DeviceConfigureZone(1);
    else throw std::runtime_error("unknown case");
    const auto failed = Receipt(api); auto count = writes.size();
    Check(failed["stage"] == 3, "failure not sticky");
    api->UpdateMode(); api->UpdateLEDs(); api->UpdateZoneLEDs(0); api->UpdateSingleLED(0);
    Check(writes.size() == count && Receipt(api) == failed, "later request overwrote failure or wrote HID");
}
int main(int argc, char** argv)
{
    try { Check(argc == 2, "case required"); Run(argv[1]); Check(closes == 1, "native handle retained"); std::cout << "PASS " << argv[1] << '\n'; return 0; }
    catch(const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
