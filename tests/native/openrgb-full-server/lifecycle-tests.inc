// No bootstrap, controller detection or HID entry point is called by these cases.
#include <future>
#include <iostream>
#include <condition_variable>
#include <functional>

static std::mutex lifecycle_test_mutex;
static std::condition_variable lifecycle_test_cv;
static std::string held_point;
struct LifecycleGate { bool entered = false; bool released = false; };
static std::map<std::string, LifecycleGate> gates;
static unsigned int marked_stops = 0;
static int live_clients = 0;
static std::map<std::string, unsigned int> lifecycle_points;

void FlowDeckNetworkPoint(const char* point)
{
    std::unique_lock<std::mutex> lock(lifecycle_test_mutex);
    const std::string name(point);
    ++lifecycle_points[name];
    if(name == "client-created") ++live_clients;
    if(name == "client-destroyed") --live_clients;
    if(name == "stop-marked") ++marked_stops;
    if(auto found = gates.find(name); found != gates.end() && !found->second.released)
    {
        found->second.entered = true;
        lifecycle_test_cv.notify_all();
        lifecycle_test_cv.wait(lock, [&] { return found->second.released; });
    }
    lifecycle_test_cv.notify_all();
}

static void Require(bool value, const char* error)
{
    if(!value) { std::cerr << error << std::endl; std::_Exit(21); }
}

static void Await(const std::function<bool()>& predicate, const char* error)
{
    const auto deadline = std::chrono::steady_clock::now()+std::chrono::seconds(5);
    while(!predicate())
    {
        Require(std::chrono::steady_clock::now() < deadline, error);
        std::this_thread::yield();
    }
}

static void Hold(const char* point)
{
    std::cerr << "barrier: " << point << std::endl;
    std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
    held_point = point; gates[held_point] = {};
}

static void Entered()
{
    std::unique_lock<std::mutex> lock(lifecycle_test_mutex);
    const auto name = held_point;
    Require(lifecycle_test_cv.wait_for(lock, std::chrono::seconds(5), [&] { return gates.at(name).entered; }), "barrier was not reached");
}

static void Release(const char* point = nullptr)
{
    std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
    const std::string name = point ? point : held_point;
    gates.at(name).released = true;
    if(name == held_point) held_point.clear();
    lifecycle_test_cv.notify_all();
}

static unsigned short TestPort()
{
    SOCKET reserved = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in address{}; address.sin_family = AF_INET; address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    Require(bind(reserved, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0, "reserve loopback port");
    int length = sizeof(address);
    Require(getsockname(reserved, reinterpret_cast<sockaddr*>(&address), &length) == 0, "read loopback port");
    closesocket(reserved); return ntohs(address.sin_port);
}

static void StartTestServer(NetworkServer& server, unsigned short port)
{
    server.SetHost("127.0.0.1"); server.SetPort(port); server.StartServer();
    Require(server.GetOnline() && server.GetListening(), "test server start");
}

static SOCKET ConnectTestClient(unsigned short port)
{
    SOCKET client = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in address{}; address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK); address.sin_port = htons(port);
    Require(connect(client, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0, "test client connect");
    return client;
}

static void SendTestRequest(SOCKET client, unsigned int command, unsigned int size = 0, const void* data = nullptr)
{
    NetPacketHeader header;
    InitNetPacketHeader(&header, 0, command, size);
    Require(send(client, reinterpret_cast<char*>(&header), sizeof(header), 0) == sizeof(header), "send test header");
    if(data) Require(send(client, static_cast<const char*>(data), size, 0) == static_cast<int>(size), "send test body");
}

static void CheckQuiescent(NetworkServer& server)
{
    Require(!server.GetOnline() && !server.GetListening(), "stopped status");
    Require(server.GetNumClients() == 0 && server.FlowDeckTestQuiescent(), "owned work remains after stop");
    std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
    Require(live_clients == 0, "client/queued ownership remains after stop");
}

static void StopAtBarrier(NetworkServer& server)
{
    Entered();
    unsigned int before;
    { std::lock_guard<std::mutex> lock(lifecycle_test_mutex); before = marked_stops; }
    auto stopped = std::async(std::launch::async, [&] { server.StopServer(); });
    {
        std::unique_lock<std::mutex> lock(lifecycle_test_mutex);
        Require(lifecycle_test_cv.wait_for(lock, std::chrono::seconds(5), [&] { return marked_stops > before; }), "stop not entered");
    }
    Require(stopped.wait_for(std::chrono::milliseconds(0)) == std::future_status::timeout, "stop returned while owned work was held");
    Release();
    Require(stopped.wait_for(std::chrono::seconds(5)) == std::future_status::ready, "stop did not join released work");
    stopped.get(); CheckQuiescent(server);
}

static int RunLifecycleTests()
{
    WSADATA data{}; Require(WSAStartup(MAKEWORD(2,2), &data) == 0, "test Winsock start");
    json passed = json::array();
    {
        class NamedController : public RGBController
        { public: explicit NamedController(const char* value) { name = value; } };
        NamedController first("first"), second("second");
        NetworkServer server; server.SetControllers({&first});
        server.SetHost("127.0.0.1"); server.SetPort(TestPort());
        Hold("startup-before-controllers");
        auto start = std::async(std::launch::async, [&] { server.StartServer(); }); Entered();
        Hold("controller-list-update");
        auto update = std::async(std::launch::async, [&] { server.SetControllers({&second}); }); Entered();
        unsigned int before;
        { std::lock_guard<std::mutex> lock(lifecycle_test_mutex); before = lifecycle_points["startup-controller-lock"]; }
        Release("startup-before-controllers");
        Await([&] { std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
                    return lifecycle_points["startup-controller-lock"] > before; }, "startup did not reach controller list");
        Require(start.wait_for(std::chrono::milliseconds(0)) == std::future_status::timeout, "startup bypassed configuration owner");
        Release();
        Require(update.wait_for(std::chrono::seconds(5)) == std::future_status::ready, "controller list update stuck"); update.get();
        Require(start.wait_for(std::chrono::seconds(5)) == std::future_status::ready, "startup stuck after list update"); start.get();
        Require(server.FlowDeckTestControllerName() == "second", "startup overwrote concurrently detected controller");
        server.StopServer(); CheckQuiescent(server);
        passed.push_back("startup-retains-concurrent-controller-publication");
    }
    {
        NetworkServer server; server.StopServer(); server.StopServer(); CheckQuiescent(server);
        const auto port = TestPort(); StartTestServer(server, port); server.StartServer();
        Hold("accepted-before-adoption"); auto client = ConnectTestClient(port);
        StopAtBarrier(server); closesocket(client);
        passed.push_back("stop-joins-accepted-before-adoption");
        StartTestServer(server, port); server.StopServer(); server.StopServer(); CheckQuiescent(server);
        passed.push_back("restart-and-idempotent-start-stop");
    }
    {
        NetworkServer first, second; const auto port = TestPort(); StartTestServer(first, port);
        second.SetHost("127.0.0.1"); second.SetPort(port); second.StartServer();
        CheckQuiescent(second); first.StopServer(); StartTestServer(second, port); second.StopServer(); CheckQuiescent(second);
        passed.push_back("failed-bind-cleans-up-and-restarts");
    }
    for(bool payload : {false, true})
    {
        NetworkServer server; const auto port = TestPort(); StartTestServer(server, port);
        auto client = ConnectTestClient(port);
        Await([&] { return server.GetNumClients() == 1; }, "receiver adoption");
        if(payload) { Hold("receiving-body"); SendTestRequest(client, NET_PACKET_ID_SET_CLIENT_NAME, 64); }
        else Require(send(client, "OR", 2, 0) == 2, "partial magic send");
        if(payload) StopAtBarrier(server);
        else { server.StopServer(); CheckQuiescent(server); }
        closesocket(client);
        passed.push_back(payload ? "partial-payload-stop" : "partial-header-stop");
    }
    {
        NetworkServer server; const auto port = TestPort(); StartTestServer(server, port);
        auto client = ConnectTestClient(port); Hold("worker-request");
        SendTestRequest(client, NET_PACKET_ID_PROFILEMANAGER_GET_PROFILE_LIST);
        Entered(); shutdown(client, SD_BOTH); closesocket(client);
        Await([&] { return server.GetNumClients() == 0; }, "disconnected receiver not reaped");
        { std::lock_guard<std::mutex> lock(lifecycle_test_mutex); Require(live_clients == 1, "in-flight request lost client ownership"); }
        StopAtBarrier(server); passed.push_back("disconnected-inflight-profile-request-retains-client");
    }
    {
        RGBController rgb;
        NetworkServer server; server.SetControllers({&rgb});
        const auto port = TestPort(); StartTestServer(server, port);
        auto client = ConnectTestClient(port); Hold("worker-request");
        SendTestRequest(client, NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS);
        StopAtBarrier(server); closesocket(client);
        passed.push_back("stop-joins-controller-request");
    }
    {
        RGBController rgb;
        NetworkServer server; server.SetControllers({&rgb});
        const auto port = TestPort(); StartTestServer(server, port);
        auto client = ConnectTestClient(port); Hold("worker-request");
        SendTestRequest(client, NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS); Entered();
        unsigned int queued_before, executed_before;
        { std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
          queued_before = lifecycle_points["request-queued"]; executed_before = lifecycle_points["worker-request"]; }
        const std::array<unsigned char, 128> body{};
        SendTestRequest(client, NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS, static_cast<unsigned int>(body.size()), body.data());
        Await([&] { std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
                    return lifecycle_points["request-queued"] > queued_before; }, "second request was not queued");
        StopAtBarrier(server); closesocket(client);
        { std::lock_guard<std::mutex> lock(lifecycle_test_mutex);
          Require(lifecycle_points["worker-request"] == executed_before, "pending request executed after stop"); }
        passed.push_back("pending-controller-request-is-canceled-and-released");
    }
    {
        RGBController rgb;
        NetworkServer server; server.SetControllers({&rgb});
        const auto port = TestPort(); StartTestServer(server, port);
        auto client = ConnectTestClient(port); unsigned int version = 6;
        SendTestRequest(client, NET_PACKET_ID_REQUEST_PROTOCOL_VERSION, sizeof(version), &version);
        Await([&] { return server.GetNumClients() == 1 && server.GetClientProtocolVersion(0) == 6; }, "protocol negotiation");
        Hold("notification-send"); rgb.SignalUpdate(RGBCONTROLLER_UPDATE_REASON_UPDATELEDS);
        StopAtBarrier(server); closesocket(client);
        passed.push_back("stop-joins-notification-send");
    }
    {
        NetworkServer server; const auto port = TestPort(); StartTestServer(server, port);
        for(int i = 0; i < 96; ++i)
        {
            auto client = ConnectTestClient(port);
            Await([&] { return server.GetNumClients() == 1; }, "reconnect adoption");
            shutdown(client, SD_BOTH); closesocket(client);
            Await([&] { std::lock_guard<std::mutex> lock(lifecycle_test_mutex); return live_clients == 0; }, "reconnect cleanup");
            Await([&] { return server.GetNumClients() == 0; }, "reconnect removal");
        }
        server.StopServer(); CheckQuiescent(server); passed.push_back("96-disconnects-release-receiver-and-sender");
    }
    WSACleanup();
    std::cout << json({{"passed",passed},{"count",passed.size()},{"hardwareAccessed",false}}).dump() << std::endl;
    return 0;
}
