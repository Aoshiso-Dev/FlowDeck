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
using socklen_t = int;
#include "FlowDeckAsrockSdkPeer.inc"
#define MSG_NOSIGNAL 0
const char openrgb_sdk_magic[OPENRGB_SDK_MAGIC_SIZE] = {'O','R','G','B'};
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
struct NetworkClientInfo { unsigned int client_protocol_version=6; SOCKET client_sock=INVALID_SOCKET; };
class NetworkServer
{
public:
    std::vector<RGBController*> controllers;
    std::shared_mutex controller_ids_mutex;
    std::mutex send_in_progress;
    bool legacy_workaround_enabled=false;
    SettingsManager* settings_manager=ResourceManager::get()->GetSettingsManager();
    unsigned int index_from_id(unsigned int id,unsigned int,bool* valid) { *valid=id==42; return 0; }
    NetPacketStatus ProcessRequest_RGBController_UpdateLEDs(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateZoneLEDs(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateSingleLED(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateSaveMode(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int,bool);
    NetPacketStatus ProcessRequest_FlowDeckOperation(NetworkClientInfo*,unsigned int,unsigned int);
    NetPacketStatus ProcessRequest_SettingsManager_GetSettings(NetworkClientInfo*,unsigned int,unsigned char*);
    NetPacketStatus ProcessRequest_SettingsManager_GetSettingsSchema(NetworkClientInfo*,unsigned int,unsigned char*);
    std::vector<unsigned char> Description()
    {
        auto rgb=controllers.at(0); std::shared_lock<std::shared_mutex> lock(rgb->AccessMutex);
        const auto total=4+RGBController::GetDeviceDescriptionSize(rgb,6);
        std::vector<unsigned char> data(total); memcpy(data.data(),&total,4);
        auto end=RGBController::GetDeviceDescriptionData(data.data()+4,rgb,6);
        Check(end==data.data()+total,"description size mismatch"); return data;
    }
};
#include "sdk-functions.inc"
#include "FlowDeckAsrockSdk.inc"
DetectedControllers DetectPolychromeUSBControllers(hid_device_info*,const std::string&);
class Socket
{
public:
    SOCKET value=INVALID_SOCKET;
    explicit Socket(SOCKET s) : value(s) { Check(s!=INVALID_SOCKET,"socket failed"); }
    ~Socket() { closesocket(value); }
};
static bool Receive(SOCKET s,void* data,int size)
{
    auto p=static_cast<char*>(data);
    for(int offset=0;offset<size;)
    { const auto n=recv(s,p+offset,size-offset,0); if(n<=0) return false; offset+=n; }
    return true;
}
static void Send(SOCKET s,const void* data,int size)
{
    const auto p=static_cast<const char*>(data);
    // Deliberate fragmentation also exercises the C# frame receiver.
    for(int offset=0;offset<size;)
    { const auto n=send(s,p+offset,std::min(size-offset,17),0); Check(n>0,"send failed"); offset+=n; }
}
static void Reply(SOCKET s,unsigned int id,unsigned int command,const void* data,unsigned int length)
{
    NetPacketHeader header{}; InitNetPacketHeader(&header,id,command,length);
    Send(s,&header,sizeof(header)); Send(s,data,static_cast<int>(length));
}
static void Serve(RGBController* rgb)
{
    WSADATA wsa{}; Check(WSAStartup(MAKEWORD(2,2),&wsa)==0,"Winsock startup failed");
    struct Cleanup { ~Cleanup() { WSACleanup(); } } cleanup;
    Socket listener(socket(AF_INET,SOCK_STREAM,IPPROTO_TCP));
    sockaddr_in address{}; address.sin_family=AF_INET; address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
    address.sin_port=htons(seed.value("listenPort",static_cast<unsigned short>(0)));
    Check(bind(listener.value,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0,"loopback bind failed");
    Check(listen(listener.value,1)==0,"listen failed");
    int size=sizeof(address); Check(getsockname(listener.value,reinterpret_cast<sockaddr*>(&address),&size)==0,"local endpoint failed");
    Save("native-ready.json",{{"port",ntohs(address.sin_port)},{"receipt",json::parse(rgb->FlowDeckOperationState())}});
    Socket client(accept(listener.value,nullptr,nullptr));
    const DWORD timeout=10000; setsockopt(client.value,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<const char*>(&timeout),sizeof(timeout));
    const BOOL noDelay=TRUE; setsockopt(client.value,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<const char*>(&noDelay),sizeof(noDelay));
    NetworkServer server; server.controllers.push_back(rgb); NetworkClientInfo info; info.client_sock=client.value;
    NetPacketHeader header{};
    while(Receive(client.value,&header,sizeof(header)))
    {
        Check(memcmp(&header,openrgb_sdk_magic,4)==0 && header.pkt_size<=1024*1024,"invalid fixture SDK header");
        std::vector<unsigned char> bytes(header.pkt_size); if(!Receive(client.value,bytes.data(),static_cast<int>(bytes.size()))) break;
        commands.push_back(header.pkt_id); State(); NetPacketStatus status=NET_PACKET_STATUS_OK;
        const unsigned int id=header.pkt_dev_id,command=header.pkt_id,length=header.pkt_size;
        switch(command)
        {
        case 40: { const unsigned int version=6; Reply(client.value,id,40,&version,4); break; }
        case 50: break;
        case 52: { const unsigned int flags=17; Reply(client.value,id,53,&flags,4); break; }
        case 0: { const unsigned int ids[]={1,42}; Reply(client.value,id,0,ids,sizeof(ids)); break; }
        case 1:
        {
            Check(id==42,"wrong read target"); const auto data=server.Description();
            Reply(client.value,id,1,data.data(),static_cast<unsigned int>(data.size())); break;
        }
        case NET_PACKET_ID_FLOWDECK_OPERATION: status=server.ProcessRequest_FlowDeckOperation(&info,id,length); break;
        case 250: status=server.ProcessRequest_SettingsManager_GetSettings(&info,length,bytes.data()); break;
        case 251: status=server.ProcessRequest_SettingsManager_GetSettingsSchema(&info,length,bytes.data()); break;
        case 1050: status=server.ProcessRequest_RGBController_UpdateLEDs(&info,length,bytes.data(),id); break;
        case 1051: status=server.ProcessRequest_RGBController_UpdateZoneLEDs(&info,length,bytes.data(),id); break;
        case 1052: status=server.ProcessRequest_RGBController_UpdateSingleLED(&info,length,bytes.data(),id); break;
        case 1101: status=server.ProcessRequest_RGBController_UpdateSaveMode(&info,length,bytes.data(),id,false); break;
        default: status=NET_PACKET_STATUS_ERROR_UNSUPPORTED;
        }
        Save("native-receipt.json",json::parse(rgb->FlowDeckOperationState()));
        if(command!=0 && command==seed.value("dropCommand",0u)) break;
        const unsigned int ack[]={command,static_cast<unsigned int>(status)}; Reply(client.value,id,10,ack,sizeof(ack));
    }
}
int main(int argc,char** argv)
{
    if(argc!=2 || std::string(argv[1])!="--integration-fixture") return 90;
    int result=0;
    try
    {
        std::ifstream input("native-seed.json"); input >> seed;
        path=seed.at("path").get<std::string>(); counts=seed.at("counts").get<std::array<unsigned char,8>>();
        order=seed.at("order").get<unsigned char>(); available=seed.at("available").get<unsigned char>(); raw=seed.at("raw");
        FlowDeckAsrockBootstrap::InstallFromEnvironment();
        environmentCleared=GetEnvironmentVariableW(FlowDeckAsrockBootstrap::EnvironmentName,nullptr,0)==0;
        fixtureSettings=seed.at("settings"); categories=seed.value("environment",json()); State();
        hid_device_info info={const_cast<char*>(path.c_str()),0x26ce,0x01a2};
        const auto found=DetectPolychromeUSBControllers(&info,"fixture"); Check(found.size()==1,"native detection failed");
        std::unique_ptr<RGBController> rgb(found[0]); Serve(rgb.get());
    }
    catch(const std::exception& error) { result=20; Save("native-error.json",{{"error",error.what()}}); }
    State(); Save("native-result.json",{{"code",result}});
    // Keep ownership alive while the parent checks files; its job closes on cleanup.
    for(int i=0;i<500 && !std::filesystem::exists("native-finish");++i) Sleep(20);
    return result;
}
