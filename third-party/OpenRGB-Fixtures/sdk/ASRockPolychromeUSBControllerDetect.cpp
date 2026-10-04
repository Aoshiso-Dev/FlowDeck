/*---------------------------------------------------------*\
| ASRockPolychromeUSBControllerDetect.cpp                   |
|                                                           |
|   Detector for ASRock Polychrome USB motherboards         |
|                                                           |
|   Ed Kambulow (dredvard)                      20 Dec 2020 |
|   Shady Nawara (ShadyNawara)                  16 Jan 2023 |
|                                                           |
|   This file is part of the OpenRGB project                |
|   SPDX-License-Identifier: GPL-2.0-or-later               |
\*---------------------------------------------------------*/

#include <hidapi.h>
#include <memory>
#include "ASRockPolychromeUSBController.h"
#include "DetectionManager.h"
#include "RGBController_ASRockPolychromeUSB.h"
#include "FlowDeckHidAllowlist.h"
#include "FlowDeckAsrockAuthorization.h"
#include "ResourceManager.h"
#include "SettingsManager.h"

/*---------------------------------------------------------*\
| ASRock vendor ID                                          |
\*---------------------------------------------------------*/
#define ASROCK_VID                                  0x26CE

/*---------------------------------------------------------*\
| ASRock product ID                                         |
\*---------------------------------------------------------*/
#define ASROCK_MOTHERBOARD_1_PID                    0x01A2
#define ASROCK_DESKMINI_ADDRESSABLE_LED_STRIP_PID   0x01A6

DetectedControllers DetectPolychromeUSBControllers(hid_device_info* info, const std::string& /*name*/)
{
    DetectedControllers detected_controllers;

    // FlowDeck patch 1: no path outside the durable pre-detection capture may open.
    // The startup snapshot is immutable and each path can be attempted only once per process.
    if(info == nullptr || info->vendor_id != ASROCK_VID || info->product_id != ASROCK_MOTHERBOARD_1_PID)
        return detected_controllers;
    static FlowDeckHidAllowlist captured([]() -> nlohmann::json
    {
        try
        {
            auto settings = ResourceManager::get()->GetSettingsManager()->GetSettings("ASRock Polychrome USB");
            if(settings.is_object() && settings.contains("FlowDeck")) return settings["FlowDeck"];
        }
        catch(...) { }
        return nullptr;
    }());
    if(!captured.TryClaim(info->path)) return detected_controllers;
    FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeOpen, info->path);

    // The detector owns HID until the native controller is constructed; that controller
    // owns it thereafter. Constructor failures cannot leak a handle or release ownership early.
    std::unique_ptr<hid_device, decltype(&hid_close)> device(hid_open_path(info->path), hid_close);
    if(device)
    {
        auto controller = std::make_unique<PolychromeUSBController>(device.get(), info->path);
        device.release();
        auto rgb_controller = std::make_unique<RGBController_PolychromeUSB>(controller.get());
        controller.release();
        detected_controllers.push_back(rgb_controller.get());
        rgb_controller.release();
    }

    return(detected_controllers);
}

REGISTER_HID_DETECTOR("ASRock Polychrome USB",                  DetectPolychromeUSBControllers, ASROCK_VID, ASROCK_MOTHERBOARD_1_PID);
REGISTER_HID_DETECTOR("ASRock Deskmini Addressable LED Strip",  DetectPolychromeUSBControllers, ASROCK_VID, ASROCK_DESKMINI_ADDRESSABLE_LED_STRIP_PID);
