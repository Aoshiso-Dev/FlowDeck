#include "FlowDeckAsrockWire.h"
DetectedControllers DetectPolychromeUSBControllers(hid_device_info*, const std::string&);

static void RunDetectionTrace(const std::string& name)
{
    if(name == "startup-empty") counts[0] = 0;
    if(name == "startup-unavailable") counts[0] = 0x1e;
    order = 0xfd;
    fixtureSettings["RGSwap"] = json::object();
    for(int i = 0; i < 8; ++i) fixtureSettings["RGSwap"][polychrome_USB_zone_names[i]] = false;
    fixtureSettings["FlowDeck"] = {{"version",1},{"hid_paths",{"fixture"}}};
    json trace = json::array(); std::uint64_t sequence = 0;
    FlowDeckAsrockAuthorization::Install([&](const FlowDeckAsrockAuthorizationRequest& request)
    { trace.push_back(json::parse(FlowDeckAsrockWire::Request(++sequence, request))); });
    hid_device_info info = {const_cast<char*>("fixture"),0x26ce,0x01a2};
    auto found = DetectPolychromeUSBControllers(&info,"fixture");
    Check(found.size() == 1, "actual detector did not construct one RGB controller");
    auto rgb = std::unique_ptr<RGBController>(found[0]);
    const auto receipt = Receipt(rgb.get());
    Check(trace.size() == 1 + writes.size()*3 && serialReads == 0, "unexpected startup authorization trace");
    Check(receipt["stage"] == 0 && receipt["sequence"] == 0 && receipt["after"]["completed"] == reads, "startup baseline mismatch");
    Check(DetectPolychromeUSBControllers(&info,"fixture").empty(), "startup path opened twice");
    std::cout << "DETECTIONWIRE " << json({{"case",name},{"requests",trace},{"receipt",receipt},{"counts",counts},
        {"available",5},{"order",0xfd}}).dump() << '\n';
}
