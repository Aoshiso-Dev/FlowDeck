#define COPY_DATA_FIELD(data_ptr, data_start, field)                                \
    if((size_t)(data_ptr + sizeof(field) - data_start) <= (size_t)data_size)        \
    {                                                                               \
        memcpy(&field, data_ptr, sizeof(field));                                    \
        data_ptr += sizeof(field);                                                  \
    }                                                                               \
    else                                                                            \
    {                                                                               \
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);                               \
    }                                                                               \

#define COPY_STRING_FIELD(data_ptr, data_start, length, field)                      \
    if((size_t)(data_ptr + length - data_start) <= (size_t)data_size)               \
    {                                                                               \
        field.assign((char *)data_ptr, length);                                     \
        field = StringUtils::remove_null_terminating_chars(field);                  \
        data_ptr += length;                                                         \
    }                                                                               \
    else                                                                            \
    {                                                                               \
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);                               \
    }                                                                               \

#define COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, field, controller)             \
    if((size_t)(data_ptr + sizeof(field) - data_start) <= (size_t)data_size)        \
    {                                                                               \
        memcpy(&field, data_ptr, sizeof(field));                                    \
        data_ptr += sizeof(field);                                                  \
    }                                                                               \
    else                                                                            \
    {                                                                               \
        controller->AccessMutex.unlock();                                           \
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);                               \
    }                                                                               \

/*---------------------------------------------------------*\
| NetworkServer name for log entries                        |
\*---------------------------------------------------------*/
const char* NETWORKSERVER = "NetworkServer";


NetPacketStatus NetworkServer::ProcessRequest_RGBController_UpdateLEDs(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, unsigned int controller_id)
{
    /*-----------------------------------------------------*\
    | Convert ID to index                                   |
    \*-----------------------------------------------------*/
    bool            controller_idx_valid;
    unsigned int    controller_idx          = index_from_id(controller_id, client_info->client_protocol_version, &controller_idx_valid);
    unsigned int    data_size_pkt;
    unsigned char*  data_start              = data_ptr;

    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | If controller ID is invalid, return                   |
    \*-----------------------------------------------------*/
    if(!controller_idx_valid)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_ID);
    }

    FlowDeckAsrockOrigin flowdeck_origin;
    if(controllers[controller_idx]->FlowDeckManaged() && (!FlowDeckAsrockSdkPeer(
        client_info->client_sock, client_info->client_protocol_version, 1050, controller_id, flowdeck_origin)))
        return(NET_PACKET_STATUS_ERROR_NOT_ALLOWED);
    const FlowDeckAsrockOriginScope flowdeck_origin_scope(flowdeck_origin);

    /*-----------------------------------------------------*\
    | Copy in data size                                     |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD(data_ptr, data_start, data_size_pkt);

    /*-----------------------------------------------------*\
    | If packet size is invalid, return                     |
    \*-----------------------------------------------------*/
    if(!(legacy_workaround_enabled && (client_info->client_protocol_version <= 4)) && (data_size_pkt != data_size))
    {
        LOG_ERROR("[%s] UpdateLEDs packet has invalid size. Packet size: %d", NETWORKSERVER, data_size);

        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Lock access mutex                                     |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.lock();

    /*-----------------------------------------------------*\
    | Update colors                                         |
    \*-----------------------------------------------------*/
    data_ptr = RGBController::SetColorDescription(data_ptr, data_size - (unsigned int)(data_ptr - data_start), controllers[controller_idx], client_info->client_protocol_version);

    /*-----------------------------------------------------*\
    | Unlock access mutex                                   |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.unlock();

    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Call UpdateLEDs on the given controller               |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->UpdateLEDs();

    return(controllers[controller_idx]->FlowDeckLastOperationSucceeded() ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC);
}

NetPacketStatus NetworkServer::ProcessRequest_RGBController_UpdateZoneLEDs(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, unsigned int controller_id)
{
    /*-----------------------------------------------------*\
    | Convert ID to index                                   |
    \*-----------------------------------------------------*/
    bool            controller_idx_valid;
    unsigned int    controller_idx          = index_from_id(controller_id, client_info->client_protocol_version, &controller_idx_valid);
    unsigned int    data_size_pkt;
    unsigned char*  data_start              = data_ptr;
    unsigned short  num_colors;
    unsigned int    zone_idx;

    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | If controller ID is invalid, return                   |
    \*-----------------------------------------------------*/
    if(!controller_idx_valid)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_ID);
    }

    FlowDeckAsrockOrigin flowdeck_origin;
    if(controllers[controller_idx]->FlowDeckManaged() && (!FlowDeckAsrockSdkPeer(
        client_info->client_sock, client_info->client_protocol_version, 1051, controller_id, flowdeck_origin)))
        return(NET_PACKET_STATUS_ERROR_NOT_ALLOWED);
    const FlowDeckAsrockOriginScope flowdeck_origin_scope(flowdeck_origin);

    /*-----------------------------------------------------*\
    | Copy in data size                                     |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD(data_ptr, data_start, data_size_pkt);

    /*-----------------------------------------------------*\
    | If packet size is invalid, return                     |
    \*-----------------------------------------------------*/
    if(!(legacy_workaround_enabled && (client_info->client_protocol_version <= 4)) && (data_size_pkt != data_size))
    {
        LOG_ERROR("[%s] UpdateZoneLEDs packet has invalid size. Packet size: %d", NETWORKSERVER, data_size);

        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Lock access mutex                                     |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.lock();

    /*-----------------------------------------------------*\
    | Copy in zone index                                    |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, zone_idx, controllers[controller_idx]);

    /*-----------------------------------------------------*\
    | Check if we aren't reading beyond the list of zones.  |
    \*-----------------------------------------------------*/
    if(((std::size_t)zone_idx) >= controllers[controller_idx]->zones.size())
    {
        /*-------------------------------------------------*\
        | Unlock access mutex                               |
        \*-------------------------------------------------*/
        controllers[controller_idx]->AccessMutex.unlock();
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Copy in number of colors                              |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, num_colors, controllers[controller_idx]);

    /*-----------------------------------------------------*\
    | Copy in colors                                        |
    \*-----------------------------------------------------*/
    if(((std::size_t)num_colors) > controllers[controller_idx]->zones[zone_idx].leds_count)
    {
        /*-------------------------------------------------*\
        | Unlock access mutex                               |
        \*-------------------------------------------------*/
        controllers[controller_idx]->AccessMutex.unlock();
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    for(unsigned short color_idx = 0; color_idx < num_colors; color_idx++)
    {
        COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, controllers[controller_idx]->zones[zone_idx].colors[color_idx], controllers[controller_idx]);
    }

    /*-----------------------------------------------------*\
    | Unlock access mutex                                   |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.unlock();

    /*-----------------------------------------------------*\
    | Call UpdateZoneLEDs                                   |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->UpdateZoneLEDs(zone_idx);

    return(controllers[controller_idx]->FlowDeckLastOperationSucceeded() ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC);
}

NetPacketStatus NetworkServer::ProcessRequest_RGBController_UpdateSingleLED(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, unsigned int controller_id)
{
    /*-----------------------------------------------------*\
    | Convert ID to index                                   |
    \*-----------------------------------------------------*/
    bool            controller_idx_valid;
    unsigned int    controller_idx          = index_from_id(controller_id, client_info->client_protocol_version, &controller_idx_valid);
    unsigned char*  data_start              = data_ptr;
    int             led_idx;

    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | If packet size is invalid, return                     |
    \*-----------------------------------------------------*/
    if(data_size != (sizeof(led_idx) + sizeof(RGBColor)))
    {
        LOG_ERROR("[%s] UpdateSingleLED packet has invalid size. Packet size: %d", NETWORKSERVER, data_size);

        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | If controller ID is invalid, return                   |
    \*-----------------------------------------------------*/
    if(!controller_idx_valid)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_ID);
    }

    FlowDeckAsrockOrigin flowdeck_origin;
    if(controllers[controller_idx]->FlowDeckManaged() && (!FlowDeckAsrockSdkPeer(
        client_info->client_sock, client_info->client_protocol_version, 1052, controller_id, flowdeck_origin)))
        return(NET_PACKET_STATUS_ERROR_NOT_ALLOWED);
    const FlowDeckAsrockOriginScope flowdeck_origin_scope(flowdeck_origin);

    /*-----------------------------------------------------*\
    | Lock access mutex                                     |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.lock();

    /*-----------------------------------------------------*\
    | Fixed size descrption:                                |
    |       int:      LED index                             |
    |       RGBColor: LED color                             |
    |                                                       |
    | Copy in LED index                                     |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, led_idx, controllers[controller_idx]);

    /*-----------------------------------------------------*\
    | Check if we aren't reading beyond the list of leds.   |
    \*-----------------------------------------------------*/
    if(((size_t)led_idx) >= controllers[controller_idx]->leds.size())
    {
        /*-------------------------------------------------*\
        | Unlock access mutex                               |
        \*-------------------------------------------------*/
        controllers[controller_idx]->AccessMutex.unlock();
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Copy in LED color                                     |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD_UNLOCK(data_ptr, data_start, controllers[controller_idx]->colors[led_idx], controllers[controller_idx]);

    /*-----------------------------------------------------*\
    | Unlock access mutex                                   |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.unlock();

    /*-----------------------------------------------------*\
    | Call UpdateSingleLED                                  |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->UpdateSingleLED(led_idx);

    return(controllers[controller_idx]->FlowDeckLastOperationSucceeded() ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC);
}

NetPacketStatus NetworkServer::ProcessRequest_RGBController_UpdateSaveMode(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, unsigned int controller_id, bool save_mode)
{
    /*-----------------------------------------------------*\
    | Convert ID to index                                   |
    \*-----------------------------------------------------*/
    bool            controller_idx_valid;
    unsigned int    controller_idx          = index_from_id(controller_id, client_info->client_protocol_version, &controller_idx_valid);
    unsigned int    data_size_pkt;
    unsigned char*  data_start              = data_ptr;
    int             mode_idx;
    mode            temp_mode;

    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | If controller ID is invalid, return                   |
    \*-----------------------------------------------------*/
    if(!controller_idx_valid)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_ID);
    }

    FlowDeckAsrockOrigin flowdeck_origin;
    if(controllers[controller_idx]->FlowDeckManaged() && (save_mode || !FlowDeckAsrockSdkPeer(
        client_info->client_sock, client_info->client_protocol_version, 1101, controller_id, flowdeck_origin)))
        return(NET_PACKET_STATUS_ERROR_NOT_ALLOWED);
    const FlowDeckAsrockOriginScope flowdeck_origin_scope(flowdeck_origin);

    /*-----------------------------------------------------*\
    | Copy in data size                                     |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD(data_ptr, data_start, data_size_pkt);

    /*-----------------------------------------------------*\
    | If packet size is invalid, return                     |
    \*-----------------------------------------------------*/
    if(!(legacy_workaround_enabled && (client_info->client_protocol_version <= 4)) && (data_size_pkt != data_size))
    {
        LOG_ERROR("[%s] UpdateSaveMode packet has invalid size. Packet size: %d", NETWORKSERVER, data_size);

        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Copy in mode index                                    |
    \*-----------------------------------------------------*/
    COPY_DATA_FIELD(data_ptr, data_start, mode_idx);

    /*-----------------------------------------------------*\
    | Lock access mutex                                     |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.lock();

    /*-----------------------------------------------------*\
    | Check if we aren't reading beyond the list of modes.  |
    \*-----------------------------------------------------*/
    if(((std::size_t)mode_idx) >= controllers[controller_idx]->modes.size())
    {
        /*-------------------------------------------------*\
        | Unlock access mutex                               |
        \*-------------------------------------------------*/
        controllers[controller_idx]->AccessMutex.unlock();
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Read mode description                                 |
    \*-----------------------------------------------------*/
    data_ptr = RGBController::SetModeDescription(data_ptr, data_size - (unsigned int)(data_ptr - data_start), &temp_mode, client_info->client_protocol_version);

    /*-----------------------------------------------------*\
    | Set mode values and active mode                       |
    \*-----------------------------------------------------*/
    if((mode_idx >= 0) && ((std::size_t)mode_idx < controllers[controller_idx]->modes.size()))
    {
        if(RGBController::SetModeValuesFromMode(controllers[controller_idx]->modes[mode_idx], temp_mode))
        {
            controllers[controller_idx]->active_mode = mode_idx;
        }
    }

    /*-----------------------------------------------------*\
    | Unlock access mutex                                   |
    \*-----------------------------------------------------*/
    controllers[controller_idx]->AccessMutex.unlock();

    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    /*-----------------------------------------------------*\
    | Call either SaveMode or UpdateMode on the given       |
    | controller                                            |
    \*-----------------------------------------------------*/
    if(save_mode)
    {
        controllers[controller_idx]->SaveMode();
    }
    else
    {
        controllers[controller_idx]->UpdateMode();
    }

    return(controllers[controller_idx]->FlowDeckLastOperationSucceeded() ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC);
}

NetPacketStatus NetworkServer::ProcessRequest_SettingsManager_GetSettings(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr)
{
    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    if(settings_manager != NULL)
    {
        std::string settings_key;
        settings_key.assign((char*)data_ptr, data_size);
        settings_key = StringUtils::remove_null_terminating_chars(settings_key);

        nlohmann::json settings_json = settings_manager->GetSettings(settings_key);
        std::string settings_json_str = settings_json.dump();

        NetPacketHeader reply_hdr;

        InitNetPacketHeader(&reply_hdr, 0, NET_PACKET_ID_SETTINGSMANAGER_GET_SETTINGS, (unsigned int)strlen(settings_json_str.c_str()) + 1);

        send_in_progress.lock();
        send(client_info->client_sock, (char *)&reply_hdr, sizeof(NetPacketHeader), MSG_NOSIGNAL);
        send(client_info->client_sock, (char *)settings_json_str.c_str(), reply_hdr.pkt_size, MSG_NOSIGNAL);
        send_in_progress.unlock();

        return(NET_PACKET_STATUS_OK);
    }

    return(NET_PACKET_STATUS_ERROR_UNSUPPORTED);
}

NetPacketStatus NetworkServer::ProcessRequest_SettingsManager_GetSettingsSchema(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr)
{
    /*-----------------------------------------------------*\
    | If data pointer is null, return                       |
    \*-----------------------------------------------------*/
    if(data_ptr == NULL)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_DATA);
    }

    if(settings_manager != NULL)
    {
        std::string settings_key;
        settings_key.assign((char*)data_ptr, data_size);
        settings_key = StringUtils::remove_null_terminating_chars(settings_key);

        nlohmann::json settings_json = settings_manager->GetSettingsSchema(settings_key);
        std::string settings_json_str = settings_json.dump();

        NetPacketHeader reply_hdr;

        InitNetPacketHeader(&reply_hdr, 0, NET_PACKET_ID_SETTINGSMANAGER_GET_SETTINGS_SCHEMA, (unsigned int)strlen(settings_json_str.c_str()) + 1);

        send_in_progress.lock();
        send(client_info->client_sock, (char *)&reply_hdr, sizeof(NetPacketHeader), MSG_NOSIGNAL);
        send(client_info->client_sock, (char *)settings_json_str.c_str(), reply_hdr.pkt_size, MSG_NOSIGNAL);
        send_in_progress.unlock();

        return(NET_PACKET_STATUS_OK);
    }

    return(NET_PACKET_STATUS_ERROR_UNSUPPORTED);
}

void InitNetPacketHeader
    (
    NetPacketHeader *   pkt_hdr,
    unsigned int        pkt_dev_id,
    unsigned int        pkt_id,
    unsigned int        pkt_size
    )
{
    memcpy(pkt_hdr->pkt_magic, openrgb_sdk_magic, sizeof(openrgb_sdk_magic));

    pkt_hdr->pkt_dev_id   = pkt_dev_id;
    pkt_hdr->pkt_id       = pkt_id;
    pkt_hdr->pkt_size     = pkt_size;
}