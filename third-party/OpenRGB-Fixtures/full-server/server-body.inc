// Full server and settings implementation, with only test-owned synthetic HID.
#if FLOWDECK_PATCH >= 8
#include "FlowDeckShutdown.h"
static std::unique_ptr<FlowDeckShutdown> ownedShutdown;
#endif
DetectedControllers DetectPolychromeUSBControllers(hid_device_info*, const std::string&);
static void ServeFull(RGBController* rgb)
{
    const auto port = seed.at("listenPort").get<unsigned short>();
    Check(port >= 1024, "fixture requires an explicit loopback port");
    NetworkServer server;
    server.SetHost("127.0.0.1"); server.SetPort(port);
    server.SetName("FlowDeck full server fixture");
    server.SetSettingsManager(ResourceManager::get()->GetSettingsManager());
    server.SetControllers({rgb}); server.StartServer();
    Check(server.GetOnline(), "full server could not start");
    for(int i=0; i<500 && !server.GetListening(); ++i) Sleep(10);
    Check(server.GetListening(), "full server did not listen");
    Save("native-ready.json", {{"port",port},{"receipt",json::parse(rgb->FlowDeckOperationState())}});
#if FLOWDECK_PATCH >= 8
    if(ownedShutdown) ownedShutdown->RunServerUntilStopped(&server);
    else
#endif
    for(int i=0; i<1500 && !std::filesystem::exists("native-finish"); ++i) Sleep(20);
    server.StopServer();
    Save("native-server-stopped.json", {{"online",server.GetOnline()},{"clients",server.GetNumClients()}});
}
int main(int argc, char** argv)
{
#if FLOWDECK_PATCH >= 7
    if(argc == 2 && std::string(argv[1]) == "--lifecycle-fixture") return RunLifecycleTests();
#endif
    bool useOwnedShutdown = false;
#if FLOWDECK_PATCH >= 8
    useOwnedShutdown = argc == 2 && std::string(argv[1]) == "--owned-shutdown-fixture";
#endif
    if(argc!=2 || (!useOwnedShutdown && std::string(argv[1])!="--integration-fixture")) return 90;
    int result=0;
    try
    {
        std::ifstream input("native-seed.json"); input >> seed;
        path=seed.at("path").get<std::string>(); counts=seed.at("counts").get<std::array<unsigned char,8>>();
        order=seed.at("order").get<unsigned char>(); available=seed.at("available").get<unsigned char>(); raw=seed.at("raw");
        FlowDeckAsrockBootstrap::InstallFromEnvironment();
#if FLOWDECK_PATCH >= 8
        if(useOwnedShutdown) ownedShutdown = std::make_unique<FlowDeckShutdown>();
#endif
        environmentCleared=GetEnvironmentVariableW(FlowDeckAsrockBootstrap::EnvironmentName,nullptr,0)==0;
        fixtureSettings=seed.at("settings"); categories=seed.at("environment");
        Save("OpenRGB.json",categories);
        auto settings_manager=ResourceManager::get()->GetSettingsManager();
        settings_manager->LoadSettings("OpenRGB.json");
#include "fixture-schema.inc"
        // Exercise the pinned disk writer/reader, not just its in-memory settings getters.
        settings_manager->SaveSettings();
        settings_manager->LoadSettings("OpenRGB.json");
        State();
        hid_device_info info={const_cast<char*>(path.c_str()),0x26ce,0x01a2};
        const auto found=DetectPolychromeUSBControllers(&info,"fixture"); Check(found.size()==1,"native detection failed");
        std::unique_ptr<RGBController> rgb(found[0]); ServeFull(rgb.get());
    }
    catch(const std::exception& error) { result=20; Save("native-error.json",{{"error",error.what()}}); }
    State(); Save("native-result.json",{{"code",result}});
    // Let the parent read final files before its owned-job cleanup releases this process.
    if(!useOwnedShutdown) for(int i=0;i<500 && !std::filesystem::exists("native-exit");++i) Sleep(20);
    return result;
}
