static void RunSdkAuthorization(const std::string& name)
{
    using Phase = FlowDeckAsrockAuthorizationPhase;
    using Request = FlowDeckAsrockAuthorizationRequest;
    std::vector<Request> checked;
    bool controlling = false; int deny_attempt = -1; Phase deny_phase = Phase::BeforeWrite;
    FlowDeckAsrockAuthorization::Install([&](const Request& r)
    {
        if(!controlling) { Check(!r.origin.sdk,"discovery attributed to SDK"); return; }
        checked.push_back(r);
        Check(r.path == "fixture" && r.origin.sdk && r.origin.peer_port == 51000 && r.origin.server_port == 6742 &&
              r.origin.protocol == 6 && r.origin.controller == 42, "unauthorized operation or socket");
        if(static_cast<int>(r.io.attempted) == deny_attempt && r.phase == deny_phase) throw std::runtime_error("operation authorization revoked");
    });
    if(name == "auth-partial-direct") counts[2] = 100;
    auto rgb = Open(); NetworkServer server; server.controllers.push_back(rgb.get()); NetworkClientInfo client;
    if(name == "auth-partial-direct") { rgb->StageMode(15); rgb->UpdateMode(); Check(rgb->FlowDeckLastOperationSucceeded(),"Direct preparation failed"); }
    controlling = true; const auto initial = Receipt(rgb.get()); const auto write_count = writes.size(); const auto read_count = reads;
    if(name.rfind("auth-peer-",0)==0)
    {
        if(name == "auth-peer-get-fails") peerError=-1;
        if(name == "auth-peer-local-fails") localError=-1;
        if(name == "auth-peer-ipv6") peerFamily=23;
        if(name == "auth-peer-remote-address") peerAddress=0x7f000002;
        if(name == "auth-peer-local-address") localAddress=0;
        if(name == "auth-peer-short-peer") peerLength=1;
        if(name == "auth-peer-short-local") localLength=1;
        if(name == "auth-peer-zero-peer") peerPort=0;
        if(name == "auth-peer-zero-local") localPort=0;
        if(name == "auth-peer-protocol") client.client_protocol_version=5;
        const auto p=server.ModePayload(); const auto mode=rgb->GetActiveMode();
        const auto status=server.ProcessRequest_RGBController_UpdateSaveMode(&client,static_cast<unsigned int>(p.size()),const_cast<unsigned char*>(p.data()),42,name == "auth-peer-save");
        Check(status==NET_PACKET_STATUS_ERROR_NOT_ALLOWED && checked.empty() && writes.size()==write_count && reads==read_count &&
              Receipt(rgb.get())==initial && rgb->GetActiveMode()==mode, "invalid origin reached parser/cache/hardware");
        std::cout << "AUTHWIRE " << json({{"case",name},{"command",1101},{"ack",static_cast<unsigned int>(status)},
            {"before",initial},{"after",Receipt(rgb.get())}}).dump() << '\n';
        return;
    }
    const bool success=name.find("-success")!=std::string::npos;
    if(name.find("-wrong-peer")!=std::string::npos) peerPort=51001;
    if(name.find("-write")!=std::string::npos) deny_attempt=reads+1;
    if(name.find("-read")!=std::string::npos) { deny_attempt=reads+1; deny_phase=Phase::BeforeRead; }
    if(name.find("-reply")!=std::string::npos) { deny_attempt=reads+1; deny_phase=Phase::AfterRead; }
    if(name == "auth-partial-direct") { deny_attempt=reads+3; deny_phase=Phase::BeforeWrite; }
    NetPacketStatus status; unsigned int command;
    if(name.find("-mode-")!=std::string::npos)
    { auto p=server.ModePayload(); status=server.ProcessRequest_RGBController_UpdateSaveMode(&client,static_cast<unsigned int>(p.size()),p.data(),42,false); command=1101; }
    else if(name.find("-single-")!=std::string::npos)
    {
        unsigned int p[2]={2,ToRGBColor(1,2,3)};
        status=server.ProcessRequest_RGBController_UpdateSingleLED(&client,sizeof(p),reinterpret_cast<unsigned char*>(p),42); command=1052;
    }
    else
    {
        const bool zone=name.find("-zone-")!=std::string::npos;
        const unsigned short count=static_cast<unsigned short>(zone ? 2u : rgb->GetLEDCount()); const unsigned int size=(zone ? 10 : 6)+4*count;
        std::vector<unsigned char> p(size); memcpy(p.data(),&size,4); unsigned int offset=4;
        if(zone) { int z=1; memcpy(p.data()+offset,&z,4); offset+=4; }
        memcpy(p.data()+offset,&count,2); offset+=2;
        for(unsigned short i=0;i<count;++i) { const auto color=ToRGBColor(1,2,3); memcpy(p.data()+offset,&color,4); offset+=4; }
        status=zone ? server.ProcessRequest_RGBController_UpdateZoneLEDs(&client,size,p.data(),42)
                    : server.ProcessRequest_RGBController_UpdateLEDs(&client,size,p.data(),42);
        command=zone ? 1051u : 1050u;
    }
    Check(!FlowDeckAsrockOriginScope::Current().sdk, "SDK context leaked from handler");
    Check(!checked.empty(),"SDK bypassed authorization");
    for(const auto& r : checked) Check(r.origin.command==command,"SDK command not correlated");
    const auto receipt=Receipt(rgb.get());
    std::cout << "AUTHWIRE " << json({{"case",name},{"command",command},{"ack",static_cast<unsigned int>(status)},
        {"before",initial},{"after",receipt}}).dump() << '\n';
    Check(status==static_cast<NetPacketStatus>(success ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC), "SDK response hid authorization failure");
    Check(receipt["stage"]==(success ? 2 : 3),"incorrect operation completion");
    if(success) { Check(writes.size()>write_count && reads>read_count,"successful authorized operation did not send"); return; }
    if(name=="auth-partial-direct")
        Check(writes.size()==write_count+2 && reads==read_count+2 && receipt["after"]["completed"]==read_count+2 &&
              receipt["after"]["attempted"]==read_count+2, "completed Direct prefix not retained");
    else if(deny_phase==Phase::BeforeWrite)
        Check(writes.size()==write_count && reads==read_count && receipt["after"]==receipt["before"], "denied write touched HID or changed IO receipt");
    else
        Check(writes.size()==write_count+1 && reads==read_count+(deny_phase==Phase::AfterRead ? 1 : 0) && receipt["after"]["faulted"]==true &&
              receipt["after"]["completed"]==read_count, "uncertain operation marked complete");
    controlling=false; const auto count=writes.size(); rgb->UpdateMode();
    Check(writes.size()==count && Receipt(rgb.get())==receipt,"failed operation was retried");
}
