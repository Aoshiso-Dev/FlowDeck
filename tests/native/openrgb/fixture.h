// Hardware/Qt-free collaborators for the byte-identical patched detector translation unit.
#pragma once
#include <nlohmann/json.hpp>
#include <atomic>
#include <stdexcept>
#include <string>
#include <vector>

extern nlohmann::json fixtureSettings;
extern bool settingsFail, openFails;
extern std::atomic<int> settingsReads, opens, initializations;
struct hid_device { };
struct hid_device_info { char* path; unsigned short vendor_id; unsigned short product_id; };
inline hid_device* hid_open_path(const char*)
{
    ++opens;
    static hid_device device;
    return openFails ? nullptr : &device;
}
inline void hid_close(hid_device*) { }
class PolychromeUSBController
{
public:
    PolychromeUSBController(hid_device* value, const char*) : device(value) { ++initializations; }
    ~PolychromeUSBController() { hid_close(device); }
private:
    hid_device* device;
};
class RGBController
{
public:
    virtual ~RGBController() = default;
};
class RGBController_PolychromeUSB : public RGBController
{
public:
    explicit RGBController_PolychromeUSB(PolychromeUSBController* value) : controller(value) { }
    ~RGBController_PolychromeUSB() override { delete controller; }
private:
    PolychromeUSBController* controller;
};
using DetectedControllers = std::vector<RGBController*>;
#define REGISTER_HID_DETECTOR(name, function, vid, pid)
class SettingsManager
{
public:
    nlohmann::json GetSettings(std::string name)
    {
        ++settingsReads;
        if(settingsFail || name != "ASRock Polychrome USB") throw std::runtime_error("unavailable settings");
        return fixtureSettings;
    }
};
class ResourceManager
{
public:
    static ResourceManager* get() { static ResourceManager value; return &value; }
    SettingsManager* GetSettingsManager() { return &settings; }
private:
    SettingsManager settings;
};
