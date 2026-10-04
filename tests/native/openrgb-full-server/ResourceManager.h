// Test-only resource owner: no detection, hardware enumeration or external clients exist.
#pragma once
#include "SettingsManager.h"
#include "NetworkClient.h"
#include "hardware-records.h"
#include "ResourceManagerCallback.h"
#include <stdexcept>
class ResourceManager
{
public:
    static ResourceManager* get() { static ResourceManager instance; return &instance; }
    SettingsManager* GetSettingsManager() { return &settings; }
    bool IsLocalClient() { return false; }
    NetworkClient* GetLocalClient() { throw std::runtime_error("fixture has no external client"); }
    unsigned int GetDetectionPercent() { return 100; }
    std::string GetDetectionString() { return ""; }
    void RescanDevices() { throw std::runtime_error("fixture cannot rescan"); }
    std::vector<HIDDeviceInfo> GetHIDDeviceInfo() { throw std::runtime_error("fixture cannot enumerate HID"); }
    std::vector<i2c_smbus_info> GetI2CBusInfo() { throw std::runtime_error("fixture cannot enumerate I2C"); }
    std::vector<std::string> GetSerialPorts() { throw std::runtime_error("fixture cannot enumerate serial ports"); }
    std::vector<SerialDeviceInfo> GetUSBSerialPorts() { throw std::runtime_error("fixture cannot enumerate serial devices"); }
    std::vector<USBDeviceInfo> GetUSBDeviceInfo() { throw std::runtime_error("fixture cannot enumerate USB"); }
private:
    SettingsManager settings;
};
