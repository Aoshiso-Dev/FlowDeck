/* FlowDeck's pinned OpenRGB helper: pre-open detection admission.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once

#include <nlohmann/json.hpp>
#include <mutex>
#include <set>
#include <string>

// Construct once from the protected startup settings. Invalid/missing configuration admits
// nothing. Consuming a path before hid_open_path prevents later scans/hotplug from reopening it.
// This bounds which paths may be opened; it does not prove continuity of the physical device.
class FlowDeckHidAllowlist
{
public:
    explicit FlowDeckHidAllowlist(const nlohmann::json& settings)
    {
        if(!settings.is_object() || settings.size() != 2 || !settings.contains("version") ||
           !settings["version"].is_number_integer() || settings["version"] != 1 ||
           !settings.contains("hid_paths") || !settings["hid_paths"].is_array() ||
           settings["hid_paths"].size() > 32)
        {
            return;
        }
        std::set<std::string> validated;
        for(const auto& item : settings["hid_paths"])
        {
            if(!item.is_string()) return;
            std::string path = item.get<std::string>();
            if(!Normalize(path) || !validated.insert(path).second) return;
        }
        paths.swap(validated);
    }

    bool TryClaim(const char* path)
    {
        if(path == nullptr) return false;
        std::size_t length = 0;
        while(length <= 2048 && path[length] != '\0') ++length;
        if(length > 2048) return false;
        std::string normalized(path, length);
        if(!Normalize(normalized)) return false;
        std::lock_guard<std::mutex> guard(mutex);
        // erase returns one only for the first admission, including concurrent callbacks.
        return paths.erase(normalized) == 1;
    }

private:
    static bool Normalize(std::string& path)
    {
        if(path.empty() || path.size() > 2048) return false;
        for(char& character : path)
        {
            const auto value = static_cast<unsigned char>(character);
            if(value < 0x21 || value > 0x7e) return false;
            if(character >= 'A' && character <= 'Z') character = static_cast<char>(character + ('a' - 'A'));
        }
        return true;
    }
    std::mutex mutex;
    std::set<std::string> paths;
};
