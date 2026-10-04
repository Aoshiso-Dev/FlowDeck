// Exercises the patched production detector, with only OS/hardware collaborators replaced.
#include "fixture.h"
#include <fstream>
#include <iostream>
#include <thread>
#if FLOWDECK_PATCH >= 5
#include "FlowDeckAsrockAuthorization.h"
#endif

nlohmann::json fixtureSettings;
bool settingsFail = false, openFails = false;
std::atomic<int> settingsReads = 0, opens = 0, initializations = 0;
DetectedControllers DetectPolychromeUSBControllers(hid_device_info*, const std::string&);

int main(int argc, char** argv)
{
    try
    {
#if FLOWDECK_PATCH >= 5
        FlowDeckAsrockAuthorization::Install([](const FlowDeckAsrockAuthorizationRequest&) { });
#endif
        if(argc != 2) throw std::runtime_error("expected one case file");
        std::ifstream input(argv[1]);
        nlohmann::json test;
        input >> test;
        fixtureSettings = test["settings"];
        settingsFail = test.value("settings_fail", false);
        for(const auto& step : test["steps"])
        {
            if(step.contains("settings")) fixtureSettings = step["settings"];
            if(step.contains("settings_fail")) settingsFail = step["settings_fail"];
            openFails = step.value("open_fails", false);
            std::string path = step.value("path", "");
            hid_device_info info = {step.value("null_path", false) ? nullptr : path.data(),
                                   step.value<unsigned short>("vid", 0x26ce), step.value<unsigned short>("pid", 0x01a2)};
            const int threads = step.value("threads", 1);
            auto detect = [&]()
            {
                auto controllers = DetectPolychromeUSBControllers(step.value("null_info", false) ? nullptr : &info, "fixture");
                for(auto controller : controllers) delete controller;
            };
            if(threads == 1) detect();
            else
            {
                std::vector<std::thread> workers;
                for(int i = 0; i < threads; ++i) workers.emplace_back(detect);
                for(auto& worker : workers) worker.join();
            }
            if(opens != step["opens"].get<int>() || initializations != step["initializations"].get<int>())
                throw std::runtime_error("unexpected hardware open/initialization count");
        }
        if(settingsReads != test["settings_reads"].get<int>()) throw std::runtime_error("startup settings were not captured exactly once");
        std::cout << "PASS " << test["name"].get<std::string>() << '\n';
        return 0;
    }
    catch(const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
