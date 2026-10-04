/* Included after NetworkServer.h provides the platform socket declarations.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "Controllers/ASRockPolychromeUSBController/FlowDeckAsrockAuthorization.h"

static bool FlowDeckAsrockSdkPeer(SOCKET socket, unsigned int protocol, unsigned int command,
                                 unsigned int controller, FlowDeckAsrockOrigin& origin)
{
    origin = {};
    if(protocol != 6 || (command != 1050 && command != 1051 && command != 1052 && command != 1101)) return false;
    sockaddr_storage remote{}, local{};
    socklen_t remote_size = sizeof(remote), local_size = sizeof(local);
    if(getpeername(socket, reinterpret_cast<sockaddr*>(&remote), &remote_size) != 0 ||
       getsockname(socket, reinterpret_cast<sockaddr*>(&local), &local_size) != 0 ||
       remote.ss_family != AF_INET || local.ss_family != AF_INET ||
       remote_size != sizeof(sockaddr_in) || local_size != sizeof(sockaddr_in)) return false;
    const auto& peer = reinterpret_cast<const sockaddr_in&>(remote);
    const auto& server = reinterpret_cast<const sockaddr_in&>(local);
    if(peer.sin_addr.s_addr != htonl(INADDR_LOOPBACK) || server.sin_addr.s_addr != htonl(INADDR_LOOPBACK) ||
       peer.sin_port == 0 || server.sin_port == 0) return false;
    origin = {true, protocol, command, controller, ntohs(server.sin_port), ntohs(peer.sin_port)};
    return true;
}
