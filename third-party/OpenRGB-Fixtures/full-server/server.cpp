// Test-only framing/settings/HID. The detector, serializer, mutation handlers, native
// controller and authorization bootstrap are compiled unmodified from verified patch 6.
#include <winsock2.h>
#include <ws2tcpip.h>
#include "fixture.h"
#include "RGBController_ASRockPolychromeUSB.h"
#include "FlowDeckAsrockBootstrap.h"
#include "NetworkProtocol.h"
#include "LogManager.h"
#include <fstream>
#include <filesystem>
#include <memory>
#include "NetworkServer.h"
json fixtureSettings;
static json seed, raw, categories, settingsRequests = json::array(), packets = json::array(), commands = json::array();
static hid_device device;
static std::string path;
static std::array<unsigned char,8> counts{};
static unsigned char order, available;
static int opens = 0, closes = 0, reads = 0;
static bool environmentCleared = false;
static void Check(bool value, const char* message) { if(!value) throw std::runtime_error(message); }
static void Save(const char* name, const json& data)
{
    const std::string temporary = std::string(name)+".tmp";
    { std::ofstream file(temporary, std::ios::binary); file << data.dump(); file.flush(); Check(file.good(), "fixture save failed"); }
    Check(MoveFileExA(temporary.c_str(),name,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0,"fixture rename failed");
}
static void State()
{
    Save("native-state.json", {{"order",order},{"available",available},{"counts",counts},{"raw",raw},
        {"opens",opens},{"closes",closes},{"reads",reads},{"packets",packets},{"commands",commands},
        {"settingsRequests",settingsRequests},{"environmentCleared",environmentCleared}});
}
json FixtureGetSettings(const std::string& key)
{
    settingsRequests.push_back(key); State();
    if(std::filesystem::exists("native-settings-override.json"))
    {
        std::ifstream file("native-settings-override.json"); json overrides; file >> overrides;
        if(overrides.contains(key)) return overrides.at(key);
    }
    if(categories.is_object() && categories.contains(key)) return categories.at(key);
    return key=="ASRock Polychrome USB" ? fixtureSettings : json();
}
json FixtureGetSchema(const std::string& key)
{
    // Registration/storage is a fixture. The actual SDK getter serializes this schema.
    settingsRequests.push_back(key+"/schema"); State();
    return key=="Detectors" ? json{{"hid_safe_mode",{{"type","bool"}}}} : json();
}
void FixtureSetSettings(const std::string& key,json value)
{
    if(categories.is_object()) categories[key]=value;
    if(key=="ASRock Polychrome USB") fixtureSettings=value;
}
hid_device* hid_open_path(const char* actual)
{ Check(path == actual && opens == 0,"unexpected native open"); ++opens; State(); return &device; }
void hid_close(hid_device* actual) { Check(actual == &device,"wrong native close"); ++closes; State(); }
int hid_write(hid_device* actual, const unsigned char* data, std::size_t length)
{
    Check(actual==&device && length==65,"wrong native write");
    const std::vector<unsigned char> packet(data,data+length); packets.push_back(packet);
    if(data[1]==0x15 && data[3]==3) order=data[4];
    else if(data[1]==0x10 && data[3]<8) raw[std::to_string(data[3])]=std::vector<unsigned char>(data+4,data+9);
    else Check(data[1]==0x14 || data[1]==0x11 || (data[1]==0x10 && data[3]==0xff && (data[4]==0xe3 || data[4]==0xe4)),"unexpected native command");
    State(); return 65;
}
int hid_read_timeout(hid_device* actual, unsigned char* data, std::size_t length, int timeout)
{
    Check(actual==&device && length==64 && timeout==700,"wrong native read"); ++reads;
    if(reads==seed.value("failReadAt",0)) { State(); return 0; }
    std::fill_n(data,length,static_cast<unsigned char>(0));
    const auto p=packets.back().get<std::vector<unsigned char>>(); data[3]=p[3];
    if(p[1]==0x14)
    {
        if(p[3]==1) data[4]=available;
        if(p[3]==2) std::copy(counts.begin(),counts.end(),data+4);
        if(p[3]==3) data[4]=order;
    }
    if(p[1]==0x11)
    { const auto value=raw.at(std::to_string(p[3])).get<std::vector<unsigned char>>(); Check(value.size()==5,"bad native seed"); std::copy(value.begin(),value.end(),data+4); }
    State(); return 64;
}

#include "lifecycle-tests.inc"

#include "server-body.inc"
