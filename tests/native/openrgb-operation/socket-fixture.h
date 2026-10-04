// Kernel socket queries only are replaced. The endpoint admission function itself is real.
#pragma once
using SOCKET = int;
using socklen_t = int;
constexpr unsigned short AF_INET = 2;
constexpr unsigned int INADDR_LOOPBACK = 0x7f000001;
struct sockaddr { unsigned short sa_family; char bytes[14]; };
struct in_addr { unsigned int s_addr; };
struct sockaddr_in { unsigned short sin_family, sin_port; in_addr sin_addr; char bytes[8]; };
struct sockaddr_storage { unsigned short ss_family; char padding[6]; std::uint64_t bytes[15]; };
static unsigned int htonl(unsigned int v) { return ((v & 255)<<24) | ((v & 0xff00)<<8) | ((v & 0xff0000)>>8) | (v>>24); }
static unsigned short ntohs(unsigned short v) { return static_cast<unsigned short>((v<<8)|(v>>8)); }
static int peerError = 0, localError = 0;
static unsigned short peerFamily = AF_INET;
static unsigned int peerAddress = INADDR_LOOPBACK, localAddress = INADDR_LOOPBACK;
static unsigned short peerPort = 51000, localPort = 6742;
static int peerLength = sizeof(sockaddr_in), localLength = sizeof(sockaddr_in);
static int getpeername(SOCKET socket, sockaddr* address, socklen_t* size)
{
    Check(socket == 1, "wrong SDK socket");
    sockaddr_in value{}; value.sin_family = peerFamily; value.sin_port = ntohs(peerPort); value.sin_addr.s_addr = htonl(peerAddress);
    memcpy(address,&value,sizeof(value)); *size=peerLength; return peerError;
}
static int getsockname(SOCKET socket, sockaddr* address, socklen_t* size)
{
    Check(socket == 1, "wrong SDK socket");
    sockaddr_in value{}; value.sin_family = AF_INET; value.sin_port = ntohs(localPort); value.sin_addr.s_addr = htonl(localAddress);
    memcpy(address,&value,sizeof(value)); *size=localLength; return localError;
}
