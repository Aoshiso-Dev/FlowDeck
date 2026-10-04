#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>

namespace WifiPanel {
void begin(const String& deviceId);
bool configure(JsonVariantConst payload, String& error);
void status(JsonDocument& document, const char* transport);
bool ready();
bool configured();
String label();
String signal();
void send(const String& frame);
bool receive(String& frame);
}
