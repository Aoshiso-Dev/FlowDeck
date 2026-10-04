"""Make pinned patch 7: own, quiesce and join the complete SDK server lifecycle."""
import argparse
import difflib
import importlib.util
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def function(text, name, contains=None):
    # Pinned source uses column-zero function boundaries, including destructors.
    pattern = r'(?ms)^[^\n;{}]*\b'+re.escape(name)+r'\([^\n]*\)\n\{.*?^\}'
    matches = list(re.finditer(pattern, text))
    if contains is not None:
        matches = [match for match in matches if contains in match.group()]
    if len(matches) != 1:
        raise ValueError(f'Expected one function {name}, got {len(matches)}')
    return matches[0]


def change_function(text, name, transform, contains=None):
    match = function(text, name, contains)
    return text[:match.start()]+transform(match.group())+text[match.end():]


def patch(archive, destination):
    module('bootstrap', 'prepare-openrgb-bootstrap-source.py').patch(archive, destination)
    once = module('operation', 'prepare-openrgb-operation-source.py').once
    prepare = module('prepare', 'prepare-openrgb-source.py')
    destination = Path(destination)
    old = {name: (destination/name).read_text(encoding='utf-8') for name in ['NetworkServer.h', 'NetworkServer.cpp']}
    header, source = old['NetworkServer.h'], old['NetworkServer.cpp']
    header = once(header, '#include <atomic>', '#include <atomic>\n#include <memory>\n#include <algorithm>')
    header = once(header, 'class NetworkClientInfo\n', 'class NetworkClientInfo : public std::enable_shared_from_this<NetworkClientInfo>\n')
    header = once(header, '    ~NetworkClientInfo();', '''    ~NetworkClientInfo();
    void Shutdown();
    void Join();
    std::atomic<bool> closing;
    std::atomic<bool> listen_finished;''')
    for field, kind in [('client_flags','unsigned int'), ('client_protocol_version','unsigned int'), ('client_is_local_client','bool')]:
        header, count = re.subn(r'\b'+kind+r'(\s+)'+field+r';', r'std::atomic<'+kind+r'>\1'+field+';', header)
        if count != 1: raise ValueError('Missing atomic field '+field)
    header = once(header, '    NetworkClientInfo*          client_info;', '''    NetworkClientInfo*          client_info;
    std::shared_ptr<NetworkClientInfo> client_owner;
    std::shared_ptr<unsigned char[]> data_owner;''')
    header = once(header, '    std::mutex                                          start_mutex;', '    std::size_t                                         queued_bytes = 0;')
    header = once(header, '    std::thread*                                        thread;', '    std::thread*                                        thread = nullptr;')
    header = once(header, '    std::atomic<bool>                                   online;', '    std::atomic<bool>                                   online{false};')
    header = once(header, 'typedef struct\n{\n    unsigned int                                        id;', 'typedef struct NetworkServerControllerThread\n{\n    unsigned int                                        id;')
    header = once(header, '    std::vector<NetworkClientInfo*>     ServerClients;', '''    std::vector<std::shared_ptr<NetworkClientInfo>> ServerClients;
    std::mutex lifecycle_mutex;
    std::mutex controller_configuration_mutex;
    std::mutex controller_routes_mutex;
    std::vector<NetworkControllerID> controller_routes;
    std::condition_variable clients_changed;
    std::thread client_reaper;
    bool reap_stopping = true;
    bool winsock_started = false;
    std::vector<std::shared_ptr<NetworkClientInfo>> ClientSnapshot();
    void ReapClients();
    void StopServerCore();
    void SetControllersCore(std::vector<RGBController*>);
    static void StopWorker(NetworkServerControllerThread*);
    bool QueueRequest(NetworkServerControllerThread*, NetworkClientInfo*, const NetPacketHeader&, std::unique_ptr<unsigned char[]>&);
    static bool PopRequest(NetworkServerControllerThread*, NetworkServerControllerThreadQueueEntry&);''')
    header = once(header, 'int                                 accept_select(int sockfd);', 'SOCKET                              accept_select(SOCKET sockfd);')
    header = once(header, 'recv_select(SOCKET s, char *buf, int len, int flags)', 'recv_select(NetworkClientInfo* client_info, char *buf, int len, int flags)')
    header = once(header, '    ~NetworkServer();', '''    ~NetworkServer();
#ifdef FLOWDECK_NETWORK_TEST
    std::string FlowDeckTestControllerName()
    {
        std::lock_guard<std::mutex> lock(controller_configuration_mutex);
        return controllers.size() == 1 ? controllers[0]->name : "";
    }
    bool FlowDeckTestQuiescent()
    {
        return socket_count == 0 && !client_reaper.joinable() && !profilemanager_thread &&
            controller_threads.empty() && ServerClients.empty() &&
            std::all_of(std::begin(ConnectionThread), std::end(ConnectionThread), [](auto thread) { return thread == nullptr; });
    }
#endif''')
    for name in ['profile_about_to_load_acks', 'profile_about_to_load_count']:
        header = re.sub(r'unsigned int(\s+)'+name+';', r'std::atomic<unsigned int>\1'+name+'{0};', header)
    source = once(source, '    server_listening            = false;', '    server_listening            = false;\n    socket_count = 0;')
    source = once(source, '        ConnectionThread[i]     = nullptr;', '        ConnectionThread[i]     = nullptr;\n        server_sock[i] = INVALID_SOCKET;')
    lifecycle = (ROOT/'native/OpenRgb/FlowDeckNetworkLifecycle.inc').read_text(encoding='utf-8')
    replaced = ['NetworkClientInfo::NetworkClientInfo', 'NetworkClientInfo::~NetworkClientInfo',
                'NetworkServer::GetNumClients', 'NetworkServer::GetClientIP', 'NetworkServer::SetControllers',
                'NetworkServer::StartServer', 'NetworkServer::StopServer',
                'NetworkServer::ConnectionThreadFunction', 'NetworkServer::accept_select']
    for name in replaced:
        source = change_function(source, name, lambda _: '')
    # All broadcast operations retain their clients after releasing the list lock.
    broadcasts = ['SignalLogManagerLoggedEntry','SetName','SignalActiveProfileChanged','SignalDetectionCompleted',
        'SignalDetectionProgress','SignalDetectionStarted','SignalDeviceListUpdated','SignalProfileListUpdated',
        'SendRequest_ProfileManager_ActiveProfileChanged','SendRequest_ProfileManager_ProfileAboutToLoad',
        'SendRequest_ProfileManager_ProfileLoaded','SendRequest_RGBController_SignalUpdate']
    for name in broadcasts:
        def broadcast(body):
            body = body.replace('    ServerClientsMutex.lock();\n', '').replace('    ServerClientsMutex.unlock();\n', '')
            body = body.replace('ServerClients', 'clients')
            body = re.sub(r'clients\[client_idx\](?!->)', 'clients[client_idx].get()', body)
            return once(body, '\n{', '\n{\n    const auto clients = ClientSnapshot();')
        source = change_function(source, 'NetworkServer::'+name, broadcast, contains='ServerClients')
    # Protocol and flags are atomic. Never hold the client-list lock around a send.
    for name in ['SendReply_ServerFlags','SendReply_ServerHostname','SendReply_ServerString']:
        source = change_function(source, 'NetworkServer::'+name, lambda body:
            body.replace('    ServerClientsMutex.lock();\n', '').replace('    ServerClientsMutex.unlock();\n', ''))
    # Queue workers wait on the queue's mutex and predicate, retaining request ownership.
    for name in ['ControllerListenThread','ProfileManagerListenThread']:
        original = function(source, 'NetworkServer::'+name).group()
        switch_start = original.index('                switch(') if name == 'ControllerListenThread' else original.index('            switch(')
        switch_end = original.index('            delete[] queue_entry.data;')
        switch = original[switch_start:switch_end].replace('                controller_ids_mutex.unlock_shared();\n', '')
        body = f'''void NetworkServer::{name}(NetworkServerControllerThread* this_thread)
{{
    NetworkServerControllerThreadQueueEntry queue_entry;
    while(PopRequest(this_thread, queue_entry))
    {{
        if(queue_entry.client_info->closing || !server_online) {{ queue_entry = {{}}; continue; }}
        FLOWDECK_NETWORK_TEST_POINT("worker-request");
        try
        {{
            NetPacketStatus status = NET_PACKET_STATUS_OK;
{switch}
            SendAck(queue_entry.client_info, queue_entry.header.pkt_dev_id, queue_entry.header.pkt_id, status);
        }}
        catch(...) {{ queue_entry.client_info->Shutdown(); }}
        queue_entry = {{}};
    }}
}}'''
        source = change_function(source, 'NetworkServer::'+name, lambda _: body)
    def listener(body):
        body = body.replace('recv_select(client_sock,', 'recv_select(client_info,')
        body = once(body, '    SOCKET client_sock = client_info->client_sock;\n', '')
        body = once(body, '        unsigned char*  data        = NULL;', '        unsigned char*  data        = NULL;\n        std::unique_ptr<unsigned char[]> packet;')
        body = once(body, '            data = new unsigned char[header.pkt_size];', '            packet = std::make_unique<unsigned char[]>(header.pkt_size);\n            data = packet.get();')
        body = once(body, '            data = packet.get();', '            data = packet.get();\n            FLOWDECK_NETWORK_TEST_POINT("receiving-body");')
        profile_start = body.index('                    profilemanager_thread->queue_mutex.lock();')
        profile_end = body.index('                    delete_data = false;', profile_start)+len('                    delete_data = false;')
        body = body[:profile_start]+'''                    delete_data = !QueueRequest(profilemanager_thread, client_info, header, packet);
                    if(delete_data) status = NET_PACKET_STATUS_ERROR_UNSUPPORTED;'''+body[profile_end:]
        controller_start = body.index('                        controller_threads[controller_thread_idx]->queue_mutex.lock();')
        controller_end = body.index('                        delete_data = false;', controller_start)+len('                        delete_data = false;')
        body = body[:controller_start]+'''                        delete_data = !QueueRequest(controller_threads[controller_thread_idx], client_info, header, packet);
                        if(delete_data) status = NET_PACKET_STATUS_ERROR_UNSUPPORTED;'''+body[controller_end:]
        body = body.replace('            delete[] data;\n', '')
        body = body[:body.index('listen_done:')]+'''listen_done:
    client_info->Shutdown();
}'''
        return body
    source = change_function(source, 'NetworkServer::ListenThreadFunction', listener)
    def notification(body):
        # The update callback can run inside a command already holding the ID lock.
        # Use a separate routing snapshot, not a recursive shared_mutex acquisition.
        start = body.index('    for(; controller_idx < controllers.size(); controller_idx++)')
        end = body.index('    /*', start)
        body = body[:start]+'''    {
        std::lock_guard<std::mutex> routes(controller_routes_mutex);
        for(const auto& route : controller_routes)
            if(route.controller == controller_ptr) { controller_id = route.id; found = true; break; }
    }

'''+body[end:]
        body = once(body, '    std::size_t                 controller_idx      = 0;\n', '')
        body = once(body, '    unsigned int                controller_id;', '    unsigned int                controller_id = 0;')
        body = once(body, '    if(found)', '    if(found && server_online)')
        body = once(body, '                    std::lock_guard<std::mutex> queue_lock(client_info->client_send_mutex);', '''                    std::lock_guard<std::mutex> queue_lock(client_info->client_send_mutex);
                    if(!client_info->client_send_running) { delete[] reply_data; continue; }''')
        return body
    source = change_function(source, 'NetworkServer::SendRequest_RGBController_SignalUpdate', notification)
    source = change_function(source, 'NetworkServer::ClientSendThreadFunction', lambda body:
        once(body, '        send_in_progress.lock();', '        FLOWDECK_NETWORK_TEST_POINT("notification-send");\n        send_in_progress.lock();'))
    source = change_function(source, 'NetworkServer::ProfileManager_ProfileAboutToLoad', lambda body:
        once(body, '            if(controller_threads[controller_thread_idx]->queue.size() > 0)', '''            std::lock_guard<std::mutex> queue_lock(controller_threads[controller_thread_idx]->queue_mutex);
            if(!controller_threads[controller_thread_idx]->queue.empty())'''))
    # Windows select need not wake promptly when another thread calls shutdown.
    # Observe cancellation at a bounded interval, while keeping ownership until join.
    def receive(body):
        body = once(body, 'recv_select(SOCKET s, char *buf, int len, int flags)', 'recv_select(NetworkClientInfo* client_info, char *buf, int len, int flags)')
        body = once(body, '\n{', '\n{\n    const SOCKET s = client_info->client_sock;')
        body = once(body, '        timeout.tv_sec          = TCP_TIMEOUT_SECONDS;', '        timeout.tv_sec          = 0;')
        body = once(body, '        timeout.tv_usec         = 0;', '        timeout.tv_usec         = 100000;')
        return once(body, 'rv == SOCKET_ERROR || server_online == false', 'rv == SOCKET_ERROR || !server_online || client_info->closing')
    source = change_function(source, 'NetworkServer::recv_select', receive)
    # Hooks are no-ops in production; the fixture injects deterministic barriers.
    source = once(source, '#include "NetworkServer.h"', '''#include "NetworkServer.h"
#ifdef FLOWDECK_NETWORK_TEST
#include "network-test-hooks.h"
#else
#define FLOWDECK_NETWORK_TEST_POINT(name) ((void)0)
#endif''')
    source += '\n#include "FlowDeckNetworkLifecycle.inc"\n'
    updates = {'NetworkServer.h':header, 'NetworkServer.cpp':source, 'FlowDeckNetworkLifecycle.inc':lifecycle}
    diff = (destination/'flowdeck.patch').read_text(encoding='utf-8')
    manifest = destination/'flowdeck-source.json'
    receipt = json.loads(manifest.read_text(encoding='utf-8'))
    entries = {item['path']:item for item in receipt['files']}
    for name, content in updates.items():
        (destination/name).write_text(content, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff(old.get(name, '').splitlines(True), content.splitlines(True), fromfile='a/'+name, tofile='b/'+name))
        data = (destination/name).read_bytes()
        entries[name] = {'path':name,'length':len(data),'sha256':prepare.sha256(data)}
    (destination/'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    receipt.update(flowDeckPatch=7, files=[entries[n] for n in sorted(entries)], patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest.write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')
    prepare.verify(destination)
    print(json.dumps({'flowDeckPatch':7,'files':len(entries),'compiled':False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive'); parser.add_argument('new_directory')
    args = parser.parse_args(); patch(args.archive, args.new_directory)
