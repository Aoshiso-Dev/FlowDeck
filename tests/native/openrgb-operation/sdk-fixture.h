// Only sockets/client inventory are mocked. Parser, RGB, and native functions are compiled from the prepared source.
#include "NetworkProtocol.h"
#include "LogManager.h"
#if FLOWDECK_PATCH >= 5
#include "socket-fixture.h"
#include "FlowDeckAsrockSdkPeer.inc"
#endif
#define MSG_NOSIGNAL 0
const char openrgb_sdk_magic[OPENRGB_SDK_MAGIC_SIZE] = {'O','R','G','B'};
struct NetworkClientInfo { unsigned int client_protocol_version = 6; int client_sock = 1; };
static std::vector<unsigned char> socketBytes;
static int sendLimit = 7, failSendAt = 0, sendCalls = 0;
static int send(int, const char* data, int size, int)
{
    if(++sendCalls == failSendAt) return -1;
    const int length = (std::min)(size, sendLimit);
    socketBytes.insert(socketBytes.end(), data, data + length); return length;
}
class NetworkServer
{
public:
    std::vector<RGBController*> controllers;
    std::shared_mutex controller_ids_mutex;
    std::mutex send_in_progress;
    bool legacy_workaround_enabled = false;
    unsigned int index_from_id(unsigned int id, unsigned int, bool* valid) { *valid = id == 42; return 0; }
    NetPacketStatus ProcessRequest_RGBController_UpdateLEDs(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateZoneLEDs(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateSingleLED(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int);
    NetPacketStatus ProcessRequest_RGBController_UpdateSaveMode(NetworkClientInfo*,unsigned int,unsigned char*,unsigned int,bool);
    NetPacketStatus ProcessRequest_FlowDeckOperation(NetworkClientInfo*,unsigned int,unsigned int);
    std::vector<unsigned char> ModePayload()
    {
        const auto m = controllers[0]->modes[2];
        const unsigned int size = 8 + RGBController::GetModeDescriptionSize(m, 6); int index = 2;
        std::vector<unsigned char> data(size);
        memcpy(data.data(), &size, 4); memcpy(data.data()+4, &index, 4);
        RGBController::GetModeDescriptionData(data.data()+8, m, 6); return data;
    }
};
#include "sdk-functions.inc"
#include "FlowDeckAsrockSdk.inc"
static void RunSdk(RGBController* api, const std::string& name)
{
    NetworkServer server; server.controllers.push_back(api); NetworkClientInfo client;
    if(name=="sdk-command-admission")
    {
        for(unsigned int command : {1050u,1051u,1052u,1101u})
        {
            Check(api->FlowDeckAcceptsCommand(command,6), "supported command refused");
            Check(!api->FlowDeckAcceptsCommand(command,5) && !api->FlowDeckAcceptsCommand(command,7), "unnegotiated protocol accepted");
        }
        for(unsigned int command : {1000u,1001u,1002u,1003u,1004u,1005u,1100u,1102u,1103u,1200u})
            Check(!api->FlowDeckAcceptsCommand(command,6), "unsupported mutation admitted");
        Check(Receipt(api)["sequence"]==0,"admission changed hardware"); return;
    }
    if(name.rfind("sdk-status-",0)==0)
    {
        unsigned int id=42, length=0;
        if(name=="sdk-status-version") client.client_protocol_version=5;
        if(name=="sdk-status-size") length=1;
        if(name=="sdk-status-id") id=43;
        if(name=="sdk-status-send-failure") failSendAt=3;
        const auto count=writes.size(); auto status=server.ProcessRequest_FlowDeckOperation(&client,id,length);
        Check(count==writes.size(), "read-only SDK status wrote hardware");
        if(name=="sdk-status-version" || name=="sdk-status-size")
        { Check(status==NET_PACKET_STATUS_ERROR_INVALID_DATA && socketBytes.empty(), "bad query accepted"); return; }
        if(name=="sdk-status-id") { Check(status==NET_PACKET_STATUS_ERROR_INVALID_ID && socketBytes.empty(), "bad target accepted"); return; }
        if(name=="sdk-status-send-failure") { Check(status==NET_PACKET_STATUS_ERROR_GENERIC, "partial send reported success"); return; }
        Check(status==NET_PACKET_STATUS_OK && sendCalls>2, "fragmented send did not finish");
        NetPacketHeader header; memcpy(&header,socketBytes.data(),sizeof(header));
        Check(header.pkt_dev_id==42 && header.pkt_id==NET_PACKET_ID_FLOWDECK_OPERATION && socketBytes.size()==sizeof(header)+header.pkt_size, "query header mismatch");
        Check(socketBytes.back()==0 && json::parse(std::string(reinterpret_cast<const char*>(socketBytes.data()+sizeof(header))))==Receipt(api), "query receipt changed");
        return;
    }
    const bool failure=name.find("failure")!=std::string::npos;
    if(failure) failReadAt=reads+1;
    NetPacketStatus status; unsigned int command;
    if(name.find("mode")!=std::string::npos)
    { auto p=server.ModePayload(); status=server.ProcessRequest_RGBController_UpdateSaveMode(&client,static_cast<unsigned int>(p.size()),p.data(),42,false); command=1101; }
    else if(name.find("single")!=std::string::npos)
    {
        unsigned int p[2]={2,ToRGBColor(1,2,3)};
        status=server.ProcessRequest_RGBController_UpdateSingleLED(&client,sizeof(p),reinterpret_cast<unsigned char*>(p),42); command=1052;
    }
    else
    {
        const bool zone=name.find("zone")!=std::string::npos;
        const unsigned short count=zone ? 2 : 3; const unsigned int size=(zone ? 10 : 6)+4*count;
        std::vector<unsigned char> data(size); memcpy(data.data(),&size,4); unsigned int offset=4;
        if(zone) { int z=1; memcpy(data.data()+offset,&z,4); offset+=4; }
        memcpy(data.data()+offset,&count,2); offset+=2;
        for(int i=0;i<count;++i) { RGBColor c=ToRGBColor(1,2,3); memcpy(data.data()+offset,&c,4); offset+=4; }
        status=zone ? server.ProcessRequest_RGBController_UpdateZoneLEDs(&client,size,data.data(),42)
                    : server.ProcessRequest_RGBController_UpdateLEDs(&client,size,data.data(),42);
        command=zone ? 1051 : 1050;
    }
    const auto r=Receipt(api);
    Check(status==static_cast<NetPacketStatus>(failure ? NET_PACKET_STATUS_ERROR_GENERIC : NET_PACKET_STATUS_OK), "SDK response hides native failure");
    Check(r["command"]==command && r["sequence"]==1 && r["stage"]==(failure ? 3 : 2), "SDK response not correlated to receipt");
    if(failure) Check(!api->FlowDeckAcceptsCommand(command,6), "failed operation still admits mutations");
}
