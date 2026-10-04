#pragma once
#include <string>
typedef struct
{
    unsigned short  vendor_id;
    unsigned short  product_id;
    unsigned short  release_number;
    unsigned short  usage_page;
    unsigned short  usage;
    int             interface_number;
    std::string     serial_number;
    std::string     manufacturer_string;
    std::string     product_string;
    std::string     path;
}   HIDDeviceInfo;

typedef struct
{
    unsigned short  vendor_id;
    unsigned short  product_id;
    std::string     serial_number;
    std::string     manufacturer_string;
    std::string     product_string;
}   USBDeviceInfo;

typedef struct
{
    char device_name[512];

    int port_id;
    int pci_device;
    int pci_vendor;
    int pci_subsystem_device;
    int pci_subsystem_vendor;

    int bus_id;
} i2c_smbus_info;
struct SerialDeviceInfo
{
    unsigned short  vendor_id;
    unsigned short  product_id;
    std::string     port_path;
    std::string     usb_path;
} ;