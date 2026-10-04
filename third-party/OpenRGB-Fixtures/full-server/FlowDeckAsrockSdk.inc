// Included only in the pinned NetworkServer.cpp. No discovery or hardware IO occurs here.
// SPDX-License-Identifier: GPL-2.0-or-later
NetPacketStatus NetworkServer::ProcessRequest_FlowDeckOperation(NetworkClientInfo* client_info, unsigned int controller_id, unsigned int data_size)
{
    if(client_info->client_protocol_version != 6 || data_size != 0)
        return NET_PACKET_STATUS_ERROR_INVALID_DATA;
    std::string text;
    {
        std::shared_lock<std::shared_mutex> lock(controller_ids_mutex);
        bool valid;
        const auto index = index_from_id(controller_id, client_info->client_protocol_version, &valid);
        if(!valid) return NET_PACKET_STATUS_ERROR_INVALID_ID;
        if(!controllers[index]->FlowDeckManaged()) return NET_PACKET_STATUS_ERROR_UNSUPPORTED;
        text = controllers[index]->FlowDeckOperationState();
    }
    NetPacketHeader reply;
    InitNetPacketHeader(&reply, controller_id, NET_PACKET_ID_FLOWDECK_OPERATION, static_cast<unsigned int>(text.size() + 1));
    std::lock_guard<std::mutex> lock(send_in_progress);
    auto send_all = [&](const char* bytes, std::size_t length)
    {
        while(length)
        {
            const int sent = send(client_info->client_sock, bytes, static_cast<int>(length), MSG_NOSIGNAL);
            if(sent <= 0) return false;
            bytes += sent; length -= sent;
        }
        return true;
    };
    return send_all(reinterpret_cast<const char*>(&reply), sizeof(reply)) && send_all(text.c_str(), text.size() + 1)
        ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC;
}
