// Mock OS/settings collaborators; the native controller, packet generation and IO guard are real.
#pragma once
#include <nlohmann/json.hpp>
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>
using json = nlohmann::json;
#include "RGBController.h"
struct hid_device { };
struct hid_device_info { char* path; unsigned short vendor_id; unsigned short product_id; };
extern json fixtureSettings;
extern bool rgbConstructorFails;
json FixtureGetSettings(const std::string&);
json FixtureGetSchema(const std::string&);
void FixtureSetSettings(const std::string&,json);
class SettingsManager
{
public:
    json GetSettings(std::string key) { return FixtureGetSettings(key); }
    json GetSettingsSchema(std::string key) { return FixtureGetSchema(key); }
    void SetSettings(std::string key, json value) { FixtureSetSettings(key,value); }
    void SaveSettings() { }
};
class ResourceManager
{
public:
    static ResourceManager* get() { static ResourceManager instance; return &instance; }
    SettingsManager* GetSettingsManager() { return &settings; }
private:
    SettingsManager settings;
};
class DMIInfo { public: std::string getMainboard() { return "Fixture board"; } };
namespace StringUtils { inline std::string remove_null_terminating_chars(std::string value) { value.erase(std::remove(value.begin(), value.end(), char(0)), value.end()); return value; } inline std::string wstring_to_string(const wchar_t*) { return ""; } }
hid_device* hid_open_path(const char*);
void hid_close(hid_device*);
int hid_write(hid_device*, const unsigned char*, std::size_t);
int hid_read_timeout(hid_device*, unsigned char*, std::size_t, int);
#if FLOWDECK_PATCH >= 5
inline int serialReads = 0;
inline int hid_get_serial_number_string(hid_device*, wchar_t*, std::size_t) { ++serialReads; return -1; }
#else
inline int hid_get_serial_number_string(hid_device*, wchar_t*, std::size_t) { return -1; }
#endif
using DetectedControllers = std::vector<RGBController*>;
#define REGISTER_HID_DETECTOR(name, function, vid, pid)
