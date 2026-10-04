/*---------------------------------------------------------*\
| RGBController_ASRockPolychromeUSB.h                       |
|                                                           |
|   RGBController for ASRock Polychrome USB motherboards    |
|                                                           |
|   Ed Kambulow (dredvard)                      20 Dec 2020 |
|   Shady Nawara (ShadyNawara)                  16 Jan 2023 |
|                                                           |
|   This file is part of the OpenRGB project                |
|   SPDX-License-Identifier: GPL-2.0-or-later               |
\*---------------------------------------------------------*/

#pragma once

#include "RGBController.h"
#include "ASRockPolychromeUSBController.h"
#include "FlowDeckAsrockOperation.h"

class RGBController_PolychromeUSB : public RGBController
{
public:
    RGBController_PolychromeUSB(PolychromeUSBController* controller_ptr);
    ~RGBController_PolychromeUSB();

    void        SetupZones();
    void        UpdateLEDs() override;
    void        UpdateMode() override;
    bool        FlowDeckManaged() const override { return true; }
    bool        FlowDeckAcceptsCommand(unsigned int command, unsigned int protocol) const override
    {
        return protocol == 6 && flowdeck_operation.Snapshot().stage != FlowDeckOperationStage::Failed &&
            (command == 1050 || command == 1051 || command == 1052 || command == 1101);
    }
    bool        FlowDeckLastOperationSucceeded() const override { return flowdeck_operation.Succeeded(); }
    std::string FlowDeckOperationState() const override { return flowdeck_operation.Json().dump(); }

    void        DeviceConfigureZone(int zone_idx);

    void        DeviceUpdateLEDs();
    void        DeviceUpdateZoneLEDs(int zone);
    void        DeviceUpdateSingleLED(int led);

    void        DeviceUpdateMode();

private:
    FlowDeckAsrockOperation flowdeck_operation;
    PolychromeUSBController*        controller;
    std::vector<PolychromeZoneInfo> zones_info;

    unsigned char GetDeviceMode(unsigned char zone);
};
