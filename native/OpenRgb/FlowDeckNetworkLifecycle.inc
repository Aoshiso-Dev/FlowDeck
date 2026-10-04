// SPDX-License-Identifier: GPL-2.0-or-later
// Joinable network ownership for the pinned FlowDeck helper.
// Test barriers are compiled only into the hardware-free fixture.
#ifndef FLOWDECK_NETWORK_TEST_POINT
#define FLOWDECK_NETWORK_TEST_POINT(name) ((void)0)
#endif

NetworkClientInfo::NetworkClientInfo()
    : closing(false), listen_finished(false), client_sock(INVALID_SOCKET), client_flags(0), client_listen_thread(nullptr),
      client_string("Client"), client_protocol_version(0), client_ip(OPENRGB_SDK_HOST),
      client_is_local(false), client_is_local_client(false), client_send_thread(nullptr),
      client_send_running(false)
{
    FLOWDECK_NETWORK_TEST_POINT("client-created");
}

void NetworkClientInfo::Shutdown()
{
    // Keep the socket handle allocated until the last queued request releases us.
    // shutdown wakes both blocking directions without permitting handle reuse.
    closing = true;
    {
        std::lock_guard<std::mutex> lock(client_send_mutex);
        client_send_running = false;
    }
    if(client_sock != INVALID_SOCKET) shutdown(client_sock, SD_BOTH);
    client_send_cv.notify_all();
}

void NetworkClientInfo::Join()
{
    for(auto slot : {&client_listen_thread, &client_send_thread})
    {
        if(*slot)
        {
            FLOWDECK_NETWORK_TEST_POINT(slot == &client_listen_thread ? "join-receive" : "join-send");
            if((*slot)->joinable()) (*slot)->join();
            FLOWDECK_NETWORK_TEST_POINT(slot == &client_listen_thread ? "joined-receive" : "joined-send");
            delete *slot;
            *slot = nullptr;
        }
    }
}

NetworkClientInfo::~NetworkClientInfo()
{
    Shutdown();
    Join();
    for(auto& entry : client_send_queue) delete[] entry.data;
    if(client_sock != INVALID_SOCKET) closesocket(client_sock);
    FLOWDECK_NETWORK_TEST_POINT("client-destroyed");
}

std::vector<std::shared_ptr<NetworkClientInfo>> NetworkServer::ClientSnapshot()
{
    std::lock_guard<std::mutex> lock(ServerClientsMutex);
    return ServerClients;
}

unsigned int NetworkServer::GetNumClients()
{
    std::lock_guard<std::mutex> lock(ServerClientsMutex);
    return static_cast<unsigned int>(ServerClients.size());
}

const char * NetworkServer::GetClientIP(unsigned int client_num)
{
    // The public API returns a pointer, so retain a copy on the calling thread.
    thread_local std::string result;
    std::lock_guard<std::mutex> lock(ServerClientsMutex);
    result = client_num < ServerClients.size() ? ServerClients[client_num]->client_ip : "";
    return result.c_str();
}

void NetworkServer::ReapClients()
{
    while(true)
    {
        std::shared_ptr<NetworkClientInfo> client;
        {
            std::unique_lock<std::mutex> lock(ServerClientsMutex);
            clients_changed.wait(lock, [this] {
                return reap_stopping || std::any_of(ServerClients.begin(), ServerClients.end(),
                    [](const auto& item) { return item->listen_finished.load(); });
            });
            if(ServerClients.empty() && reap_stopping) return;
            auto found = std::find_if(ServerClients.begin(), ServerClients.end(),
                [this](const auto& item) { return reap_stopping || item->listen_finished.load(); });
            if(found == ServerClients.end()) continue;
            client = *found;
        }
        client->Shutdown();
        client->Join(); // never join with the client-list lock held
        {
            std::lock_guard<std::mutex> lock(ServerClientsMutex);
            ServerClients.erase(std::remove(ServerClients.begin(), ServerClients.end(), client), ServerClients.end());
        }
        SignalClientInfoChanged();
    }
}

void NetworkServer::StopWorker(NetworkServerControllerThread* worker)
{
    if(!worker) return;
    {
        std::lock_guard<std::mutex> lock(worker->queue_mutex);
        worker->online = false;
    }
    worker->start_cv.notify_all();
    if(worker->thread)
    {
        worker->thread->join();
        delete worker->thread;
    }
    // Queue entries own both their payload and their client, including canceled work.
    delete worker;
}

bool NetworkServer::QueueRequest(NetworkServerControllerThread* worker, NetworkClientInfo* client,
                                const NetPacketHeader& header, std::unique_ptr<unsigned char[]>& packet)
{
    std::lock_guard<std::mutex> lock(worker->queue_mutex);
    if(!server_online || !worker->online || client->closing || worker->queue.size() >= 64 ||
       header.pkt_size > 4 * 1024 * 1024 || worker->queued_bytes + header.pkt_size > 4 * 1024 * 1024)
        return false;
    NetworkServerControllerThreadQueueEntry entry{};
    entry.header = header;
    entry.client_info = client;
    entry.client_owner = client->shared_from_this();
    entry.data_owner = std::shared_ptr<unsigned char[]>(std::move(packet));
    entry.data = entry.data_owner.get();
    worker->queue.push(std::move(entry));
    worker->queued_bytes += header.pkt_size;
    FLOWDECK_NETWORK_TEST_POINT("request-queued");
    worker->start_cv.notify_one();
    return true;
}

bool NetworkServer::PopRequest(NetworkServerControllerThread* worker, NetworkServerControllerThreadQueueEntry& entry)
{
    std::unique_lock<std::mutex> lock(worker->queue_mutex);
    worker->start_cv.wait(lock, [worker] { return !worker->online || !worker->queue.empty(); });
    if(!worker->online) return false;
    entry = std::move(worker->queue.front());
    worker->queue.pop();
    worker->queued_bytes -= entry.header.pkt_size;
    return true;
}

void NetworkServer::SetControllers(std::vector<RGBController*> new_controllers)
{
    std::lock_guard<std::mutex> configuration(controller_configuration_mutex);
    SetControllersCore(std::move(new_controllers));
}

void NetworkServer::SetControllersCore(std::vector<RGBController*> new_controllers)
{
    FLOWDECK_NETWORK_TEST_POINT("controller-list-update");
    // Remove queues from lookup before joining, without holding the lookup/ID locks.
    std::vector<NetworkServerControllerThread*> old_workers;
    {
        std::unique_lock<std::shared_mutex> lock(controller_threads_mutex);
        old_workers.swap(controller_threads);
    }
    for(auto worker : old_workers) StopWorker(worker);
    for(auto controller : controllers) controller->UnregisterUpdateCallback(this);
    {
        std::unique_lock<std::shared_mutex> ids_lock(controller_ids_mutex);
        auto prior = controller_ids;
        controllers = new_controllers;
        controller_ids.clear();
        for(auto controller : controllers)
        {
            auto found = std::find_if(prior.begin(), prior.end(),
                [controller](const auto& item) { return item.controller == controller; });
            controller_ids.push_back(found == prior.end()
                ? NetworkControllerID{controller, controller_next_idx++} : *found);
        }
        std::lock_guard<std::mutex> routes(controller_routes_mutex);
        controller_routes = controller_ids;
    }
    if(!server_online) return;
    std::unique_lock<std::shared_mutex> workers_lock(controller_threads_mutex);
    for(std::size_t index = 0; index < controller_ids.size(); ++index)
    {
        auto worker = std::make_unique<NetworkServerControllerThread>();
        worker->id = controller_ids[index].id;
        worker->index = static_cast<unsigned int>(index);
        worker->online = true;
        controller_threads.push_back(worker.get());
        auto pointer = worker.release();
        pointer->thread = new std::thread(&NetworkServer::ControllerListenThread, this, pointer);
    }
    for(auto controller : controllers) controller->RegisterUpdateCallback(RGBController_UpdateCallback, this);
}

void NetworkServer::StartServer()
{
    std::unique_lock<std::mutex> lifecycle(lifecycle_mutex);
    if(server_online) return;
    // A failed acceptor may have cleared online before its owner joins it.
    StopServerCore();
#ifdef WIN32
    if(WSAStartup(MAKEWORD(2, 2), &wsa) != NO_ERROR) return;
    winsock_started = true;
#endif
    char port_str[6];
    snprintf(port_str, sizeof(port_str), "%u", port_num);
    addrinfo hints{}, *addresses = nullptr;
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE;
    if(getaddrinfo(host.c_str(), port_str, &hints, &addresses) != 0)
    {
        StopServerCore();
        return;
    }
    bool failed = false;
    for(auto address = addresses; address && socket_count < MAXSOCK; address = address->ai_next)
    {
        SOCKET value = socket(address->ai_family, address->ai_socktype, address->ai_protocol);
        if(value == INVALID_SOCKET) { failed = true; break; }
        server_sock[socket_count++] = value;
#ifdef WIN32
        // An owned helper must not share its listening endpoint with another process.
        setsockopt(value, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, &yes, sizeof(yes));
#else
        setsockopt(value, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
#endif
        if(bind(value, address->ai_addr, static_cast<int>(address->ai_addrlen)) == SOCKET_ERROR ||
           listen(value, 10) == SOCKET_ERROR) { failed = true; break; }
    }
    freeaddrinfo(addresses);
    if(failed || socket_count == 0) { StopServerCore(); return; }
    server_online = true;
    try
    {
        FLOWDECK_NETWORK_TEST_POINT("startup-before-controllers");
        {
            // Detection may publish a controller list while startup is in progress.
            // Copy and recreate its workers under the same configuration lock.
            FLOWDECK_NETWORK_TEST_POINT("startup-controller-lock");
            std::lock_guard<std::mutex> configuration(controller_configuration_mutex);
            SetControllersCore(controllers);
        }
        profilemanager_thread = new NetworkServerControllerThread;
        profilemanager_thread->online = true;
        profilemanager_thread->thread = new std::thread(&NetworkServer::ProfileManagerListenThread, this, profilemanager_thread);
        reap_stopping = false;
        client_reaper = std::thread(&NetworkServer::ReapClients, this);
        for(int index = 0; index < socket_count; ++index)
            ConnectionThread[index] = new std::thread(&NetworkServer::ConnectionThreadFunction, this, index);
        server_listening = true;
    }
    catch(...)
    {
        StopServerCore();
        throw;
    }
    lifecycle.unlock();
    SignalServerListeningChanged();
}

void NetworkServer::StopServerCore()
{
    server_online = false;
    server_listening = false;
    FLOWDECK_NETWORK_TEST_POINT("stop-marked");
    for(int index = 0; index < socket_count; ++index)
        if(server_sock[index] != INVALID_SOCKET) shutdown(server_sock[index], SD_BOTH);
    for(auto& client : ClientSnapshot()) client->Shutdown();
    for(int index = 0; index < socket_count; ++index)
    {
        if(ConnectionThread[index])
        {
            ConnectionThread[index]->join();
            delete ConnectionThread[index];
            ConnectionThread[index] = nullptr;
        }
        if(server_sock[index] != INVALID_SOCKET) closesocket(server_sock[index]);
        server_sock[index] = INVALID_SOCKET;
    }
    socket_count = 0;
    FLOWDECK_NETWORK_TEST_POINT("stop-acceptors-joined");
    // Acceptance is fully joined; no new client can be added after this snapshot.
    for(auto& client : ClientSnapshot()) client->Shutdown();
    {
        std::lock_guard<std::mutex> lock(ServerClientsMutex);
        reap_stopping = true;
    }
    clients_changed.notify_all();
    if(client_reaper.joinable()) client_reaper.join();
    FLOWDECK_NETWORK_TEST_POINT("stop-clients-joined");
    StopWorker(profilemanager_thread);
    profilemanager_thread = nullptr;
    FLOWDECK_NETWORK_TEST_POINT("stop-profile-joined");
    {
        std::lock_guard<std::mutex> configuration(controller_configuration_mutex);
        std::vector<NetworkServerControllerThread*> workers;
        {
            std::unique_lock<std::shared_mutex> lock(controller_threads_mutex);
            workers.swap(controller_threads);
        }
        for(auto worker : workers) StopWorker(worker);
        for(auto controller : controllers) controller->UnregisterUpdateCallback(this);
    }
    FLOWDECK_NETWORK_TEST_POINT("stop-controllers-joined");
    // All queues/threads are gone before releasing the socket subsystem.
    {
        std::lock_guard<std::mutex> lock(ServerClientsMutex);
        ServerClients.clear();
    }
#ifdef WIN32
    if(winsock_started) { WSACleanup(); winsock_started = false; }
#endif
}

void NetworkServer::StopServer()
{
    {
        std::lock_guard<std::mutex> lifecycle(lifecycle_mutex);
        StopServerCore();
    }
    SignalClientInfoChanged();
    SignalServerListeningChanged();
}

void NetworkServer::ConnectionThreadFunction(int socket_idx)
{
    while(server_online)
    {
        SOCKET accepted = accept_select(server_sock[socket_idx]);
        if(accepted == INVALID_SOCKET)
        {
            server_online = false;
            server_listening = false;
            for(auto& item : ClientSnapshot()) item->Shutdown();
            break;
        }
        std::shared_ptr<NetworkClientInfo> client;
        try { client = std::make_shared<NetworkClientInfo>(); }
        catch(...) { closesocket(accepted); break; }
        client->client_sock = accepted;
        FLOWDECK_NETWORK_TEST_POINT("accepted-before-adoption");
        u_long blocking = 0;
        ioctlsocket(client->client_sock, FIONBIO, &blocking);
        setsockopt(client->client_sock, IPPROTO_TCP, TCP_NODELAY, &yes, sizeof(yes));
        sockaddr_storage address{};
        socklen_t length = sizeof(address);
        char text[INET6_ADDRSTRLEN]{};
        if(getpeername(client->client_sock, reinterpret_cast<sockaddr*>(&address), &length) == SOCKET_ERROR) continue;
        if(address.ss_family == AF_INET)
        {
            auto ipv4 = reinterpret_cast<sockaddr_in*>(&address);
            inet_ntop(AF_INET, &ipv4->sin_addr, text, sizeof(text));
            client->client_is_local = ipv4->sin_addr.s_addr == htonl(INADDR_LOOPBACK);
        }
        else if(address.ss_family == AF_INET6)
        {
            auto ipv6 = reinterpret_cast<sockaddr_in6*>(&address);
            inet_ntop(AF_INET6, &ipv6->sin6_addr, text, sizeof(text));
            client->client_is_local = IN6_IS_ADDR_LOOPBACK(&ipv6->sin6_addr);
        }
        else continue;
        client->client_ip = text;
        {
            std::lock_guard<std::mutex> lock(ServerClientsMutex);
            if(!server_online || ServerClients.size() >= 64) continue;
            ServerClients.push_back(client);
            try
            {
                client->client_send_running = true;
                client->client_send_thread = new std::thread(&NetworkServer::ClientSendThreadFunction, this, client.get());
                client->client_listen_thread = new std::thread([this, pointer = client.get()] {
                    try { ListenThreadFunction(pointer); }
                    catch(...) { pointer->Shutdown(); }
                    {
                        std::lock_guard<std::mutex> lock(ServerClientsMutex);
                        pointer->listen_finished = true;
                    }
                    clients_changed.notify_all();
                });
            }
            catch(...)
            {
                client->Shutdown();
                client->listen_finished = true;
                clients_changed.notify_all();
            }
        }
        SignalClientInfoChanged();
    }
}

SOCKET NetworkServer::accept_select(SOCKET sockfd)
{
    while(server_online)
    {
        fd_set ready;
        FD_ZERO(&ready);
        FD_SET(sockfd, &ready);
        timeval timeout{0, 100000};
#ifdef WIN32
        int status = select(0, &ready, nullptr, nullptr, &timeout);
#else
        int status = select(sockfd + 1, &ready, nullptr, nullptr, &timeout);
#endif
        if(!server_online || status == SOCKET_ERROR) return INVALID_SOCKET;
        if(status > 0) return accept(sockfd, nullptr, nullptr);
    }
    return INVALID_SOCKET;
}
