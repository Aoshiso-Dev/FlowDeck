#pragma once
// Only the hardware-free fixture defines this macro and links this function.
void FlowDeckNetworkPoint(const char* name);
#define FLOWDECK_NETWORK_TEST_POINT(name) FlowDeckNetworkPoint(name)
