// Mock OS/settings collaborators; the native controller, packet generation and IO guard are real.
#pragma once
#include <nlohmann/json.hpp>
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>
using json = nlohmann::json;
using RGBColor = std::uint32_t;
inline RGBColor ToRGBColor(unsigned char r, unsigned char g, unsigned char b) { return r | (g << 8) | (b << 16); }
inline unsigned char RGBGetRValue(RGBColor c) { return static_cast<unsigned char>(c); }
inline unsigned char RGBGetGValue(RGBColor c) { return static_cast<unsigned char>(c >> 8); }
inline unsigned char RGBGetBValue(RGBColor c) { return static_cast<unsigned char>(c >> 16); }
struct zone { std::size_t leds_count = 0; std::size_t leds_max = 0; RGBColor* colors = nullptr; };
struct hid_device { };
struct hid_device_info { char* path; unsigned short vendor_id; unsigned short product_id; };
extern json fixtureSettings;
extern bool rgbConstructorFails;
class SettingsManager
{
public:
    json GetSettings(std::string) { return fixtureSettings; }
    void SetSettings(std::string, json value) { fixtureSettings = value; }
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
namespace StringUtils { inline std::string wstring_to_string(const wchar_t*) { return ""; } }
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
class RGBController { public: virtual ~RGBController() = default; };
using DetectedControllers = std::vector<RGBController*>;
#define REGISTER_HID_DETECTOR(name, function, vid, pid)
