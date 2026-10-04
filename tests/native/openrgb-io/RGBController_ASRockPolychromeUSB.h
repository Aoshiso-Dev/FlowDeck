#pragma once
#include "fixture.h"
#include "ASRockPolychromeUSBController.h"
class RGBController_PolychromeUSB : public RGBController
{
public:
    explicit RGBController_PolychromeUSB(PolychromeUSBController* value) : controller(value)
    {
        if(rgbConstructorFails) throw std::runtime_error("fixture RGB construction failure");
        for(unsigned int i = 0; i < controller->GetChannelCount(); ++i) controller->GetZoneConfig(static_cast<unsigned char>(i));
    }
    ~RGBController_PolychromeUSB() override { delete controller; }
private:
    PolychromeUSBController* controller;
};
