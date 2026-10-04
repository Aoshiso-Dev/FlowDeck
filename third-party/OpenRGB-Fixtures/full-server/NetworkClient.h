#pragma once
#include <string>
#include <stdexcept>
class NetworkClient
{
public:
    bool GetSupportsSettingsManagerAPI() { throw std::runtime_error("unexpected external client"); }
    std::string SettingsManager_GetSettings(std::string) { throw std::runtime_error("unexpected external client"); }
    void SettingsManager_ModifySettings(std::string) { throw std::runtime_error("unexpected external client"); }
    void SettingsManager_SetSettings(std::string) { throw std::runtime_error("unexpected external client"); }
    void SettingsManager_SaveSettings() { throw std::runtime_error("unexpected external client"); }
};
