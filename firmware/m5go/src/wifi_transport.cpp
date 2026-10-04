#include "wifi_transport.h"
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <WiFiUdp.h>
#include <Preferences.h>
#include <mbedtls/x509_crt.h>
#include <mbedtls/sha256.h>
#include <atomic>

namespace WifiPanel {
namespace {
constexpr size_t kMaxFrame = 4096;
struct Config { String ssid, password, serverId, serverName, token, ca, host, caHash; int caError = 0; uint16_t port = 47631; } config;
struct Frame { char* value; uint32_t epoch; };
SemaphoreHandle_t mutex;
QueueHandle_t outgoing, incoming;
std::atomic<uint32_t> configVersion{0}, wireEpoch{0};
std::atomic<bool> authenticated{false};
std::atomic<bool> queueFault{false};
String device, phase = "未登録", ip;
int rssi = 0;
int tlsError = 0;
void lock() { xSemaphoreTake(mutex, portMAX_DELAY); }
void unlock() { xSemaphoreGive(mutex); }
bool parse(JsonVariantConst p, Config& next) {
  next.ssid = p["ssid"].as<String>(); next.password = p["password"].as<String>();
  next.serverId = p["serverId"].as<String>(); next.serverName = p["serverName"].as<String>();
  next.token = p["token"].as<String>(); next.ca = p["ca"].as<String>(); next.host = p["host"].as<String>();
  next.port = p["port"] | 47631;
  mbedtls_x509_crt certificate; mbedtls_x509_crt_init(&certificate);
  next.caError = mbedtls_x509_crt_parse(&certificate, reinterpret_cast<const unsigned char*>(next.ca.c_str()), next.ca.length() + 1);
  mbedtls_x509_crt_free(&certificate);
  unsigned char hash[32]; char hex[65];
  mbedtls_sha256_ret(reinterpret_cast<const unsigned char*>(next.ca.c_str()), next.ca.length(), hash, 0);
  for (size_t i = 0; i < 32; ++i) snprintf(hex + 2 * i, 3, "%02x", hash[i]);
  next.caHash = hex;
  IPAddress address;
  return next.ssid.length() >= 1 && next.ssid.length() <= 32 && next.password.length() >= 8 && next.password.length() <= 63 &&
    next.serverId.length() == 32 && next.serverName == "nzxt-" + next.serverId && next.token.length() == 64 &&
    next.ca.startsWith("-----BEGIN CERTIFICATE-----") && next.ca.length() < 1800 && address.fromString(next.host) && next.port == 47631;
}
void update(const String& text) {
  lock(); phase = text; ip = WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : "";
  rssi = WiFi.status() == WL_CONNECTED ? WiFi.RSSI() : 0; unlock();
}
void clear(QueueHandle_t queue) { Frame item; while (xQueueReceive(queue, &item, 0) == pdTRUE) free(item.value); }
bool enqueue(QueueHandle_t queue, const String& value) {
  if (value.length() + 1 > kMaxFrame) return false;
  Frame item{strdup(value.c_str()), wireEpoch.load()};
  if (!item.value) return false;
  if (xQueueSend(queue, &item, 0) == pdTRUE) return true;
  free(item.value); return false;
}
IPAddress discover(const Config& c) {
  IPAddress fallback; fallback.fromString(c.host);
  WiFiUDP udp; if (!udp.begin(0)) return fallback;
  const String nonce = String(esp_random(), HEX) + String(esp_random(), HEX);
  JsonDocument request; request["v"] = 1; request["type"] = "discover";
  request["serverId"] = c.serverId; request["nonce"] = nonce;
  IPAddress broadcast = WiFi.localIP(); IPAddress mask = WiFi.subnetMask();
  for (int i = 0; i < 4; ++i) broadcast[i] |= ~mask[i];
  udp.beginPacket(broadcast, 47632); serializeJson(request, udp); udp.endPacket();
  const auto start = millis();
  while (millis() - start < 600) {
    const int size = udp.parsePacket();
    if (size > 0 && size <= 512) {
      char bytes[513]; const int read = udp.read(bytes, size); bytes[read] = 0;
      JsonDocument response;
      if (!deserializeJson(response, bytes, read) && response["v"] == 1 && response["type"] == "server" &&
          response["serverId"] == c.serverId && response["nonce"] == nonce && response["port"] == c.port) {
        IPAddress peer = udp.remoteIP(); bool local = true;
        for (int i = 0; i < 4; ++i) if ((peer[i] & mask[i]) != (WiFi.localIP()[i] & mask[i])) local = false;
        if (local) { udp.stop(); return peer; }
      }
    }
    vTaskDelay(pdMS_TO_TICKS(10));
  }
  udp.stop(); return fallback;
}
void task(void*) {
  Config current; uint32_t version = UINT32_MAX; WiFiClientSecure tls;
  tls.setHandshakeTimeout(5); tls.setTimeout(2);
  char buffer[kMaxFrame]; size_t length = 0; bool dropping = false;
  uint32_t lastTry = 0, lastWifiTry = 0, lastReceive = 0, authStarted = 0, lastStatus = 0;
  bool awaitingAuth = false;
  auto close = [&]() {
    authenticated = false; awaitingAuth = false; ++wireEpoch; tls.stop();
    length = 0; dropping = false; clear(outgoing); clear(incoming);
  };
  for (;;) {
    if (queueFault.exchange(false)) { close(); update("PC再接続待ち"); lastTry = millis(); }
    if (version != configVersion.load()) {
      close(); lock(); current = config; version = configVersion.load(); unlock();
      WiFi.disconnect(true, false);
      if (!current.ssid.isEmpty()) {
        WiFi.persistent(false); WiFi.mode(WIFI_STA); WiFi.setAutoReconnect(true);
        WiFi.begin(current.ssid.c_str(), current.password.c_str());
        update("Wi-Fi接続中"); lastWifiTry = millis(); lastTry = 0;
      }
    }
    if (current.ssid.isEmpty()) { vTaskDelay(pdMS_TO_TICKS(200)); continue; }
    if (current.caError != 0) {
      lock(); tlsError = current.caError; unlock();
      update("証明書をUSBで再登録"); vTaskDelay(pdMS_TO_TICKS(200)); continue;
    }
    if (WiFi.status() != WL_CONNECTED) {
      if (authenticated || awaitingAuth) close();
      update("Wi-Fi接続待ち");
      if (millis() - lastWifiTry > 15000) { WiFi.reconnect(); lastWifiTry = millis(); }
      vTaskDelay(pdMS_TO_TICKS(100)); continue;
    }
    if (!tls.connected()) {
      if (authenticated || awaitingAuth) close();
      if (millis() - lastTry < 3000) { vTaskDelay(pdMS_TO_TICKS(20)); continue; }
      update("PC検出・TLS接続中");
      const IPAddress address = discover(current);
      // Validate the USB-provisioned private trust anchor AND its server name. Never setInsecure().
      if (!tls.connect(address, current.port, current.serverName.c_str(), current.ca.c_str(), nullptr, nullptr)) {
        char message[160]; const int error = tls.lastError(message, sizeof(message));
        lock(); tlsError = error; unlock();
        update("PC接続待ち / TLS " + String(error)); lastTry = millis(); continue;
      }
      lock(); tlsError = 0; unlock();
      ++wireEpoch; clear(outgoing); clear(incoming);
      JsonDocument auth; auth["v"] = 1; auth["type"] = "pairAuth"; auth["deviceId"] = device; auth["token"] = current.token;
      serializeJson(auth, tls); tls.write('\n'); awaitingAuth = true; authStarted = lastReceive = millis();
    }
    bool failed = false;
    int budget = 8192;
    while (budget-- > 0 && tls.available()) {
      const int value = tls.read(); if (value < 0) break;
      if (value == '\n') {
        if (!dropping && length) {
          buffer[length] = 0;
          JsonDocument message;
          if (!deserializeJson(message, buffer, length) && message["v"] == 1) {
            if (awaitingAuth && message["type"] == "pairAccepted") {
              authenticated = true; awaitingAuth = false; update("TLS認証済み");
            } else if (authenticated && !enqueue(incoming, String(buffer))) failed = true;
            lastReceive = millis();
          }
        }
        length = 0; dropping = false;
      } else if (!dropping) {
        if (length >= sizeof(buffer) - 1) { dropping = true; length = 0; }
        else buffer[length++] = static_cast<char>(value);
      }
    }
    if (authenticated) {
      if (millis() - lastStatus >= 1000) { update("TLS認証済み"); lastStatus = millis(); }
      Frame item; int count = 8;
      while (count-- > 0 && xQueueReceive(outgoing, &item, 0) == pdTRUE) {
        if (item.epoch == wireEpoch.load()) {
          const size_t size = strlen(item.value);
          if (tls.write(reinterpret_cast<const uint8_t*>(item.value), size) != size || tls.write('\n') != 1) failed = true;
        }
        free(item.value);
      }
    }
    if (failed || millis() - lastReceive > 7000 || (awaitingAuth && millis() - authStarted > 5000)) {
      close(); update("PC再接続待ち"); lastTry = millis();
    }
    vTaskDelay(pdMS_TO_TICKS(5));
  }
}
}
void begin(const String& id) {
  device = id; mutex = xSemaphoreCreateMutex(); outgoing = xQueueCreate(8, sizeof(Frame)); incoming = xQueueCreate(8, sizeof(Frame));
  if (!mutex || !outgoing || !incoming) { authenticated = false; return; }
  Preferences storage;
  if (storage.begin("nzxtwifi", true)) {
    const String json = storage.getString("config", ""); storage.end();
    JsonDocument saved;
    if (!deserializeJson(saved, json)) { Config next; if (parse(saved.as<JsonVariantConst>(), next)) { config = next; phase = "Wi-Fi接続待ち"; } }
  }
  if (xTaskCreatePinnedToCore(task, "nzxt-wifi", 12288, nullptr, 1, nullptr, 0) != pdPASS) phase = "Wi-Fi初期化失敗";
}
bool configure(JsonVariantConst payload, String& error) {
  Config next;
  if (!mutex || !parse(payload, next)) { error = "INVALID_WIFI_CONFIG"; return false; }
  if (next.caError != 0) { error = "INVALID_WIFI_CERTIFICATE"; return false; }
  JsonDocument data; data.set(payload); String json; serializeJson(data, json);
  Preferences storage;
  if (!storage.begin("nzxtwifi", false)) { error = "WIFI_STORAGE_FAILED"; return false; }
  const size_t written = storage.putString("config", json); storage.end();
  if (written != json.length()) { error = "WIFI_STORAGE_FAILED"; return false; }
  lock(); config = next; ++configVersion; unlock(); return true;
}
bool ready() { return authenticated.load(); }
bool configured() { if (!mutex) return false; lock(); const bool result = !config.ssid.isEmpty(); unlock(); return result; }
String label() { if (!mutex) return "Wi-Fi初期化失敗"; lock(); const String result = phase; unlock(); return result; }
String signal() { if (!mutex) return ""; lock(); const String result = rssi ? String(rssi) + "dBm" : ""; unlock(); return result; }
void status(JsonDocument& document, const char* transport) {
  if (!mutex) return;
  lock(); auto link = document["link"].to<JsonObject>();
  link["transport"] = transport; link["status"] = phase; link["ssid"] = config.ssid; link["ip"] = ip;
  if (rssi) link["rssi"] = rssi; else link["rssi"] = nullptr;
  link["tlsError"] = tlsError;
  // Public certificate metadata only; never expose its private key or pairing credentials.
  link["caBytes"] = config.ca.length(); link["caParseError"] = config.caError; link["caSha256"] = config.caHash;
  link["freeHeap"] = ESP.getFreeHeap(); link["maxAllocHeap"] = ESP.getMaxAllocHeap(); unlock();
}
void send(const String& frame) { if (authenticated && outgoing && !enqueue(outgoing, frame)) queueFault = true; }
bool receive(String& frame) {
  if (!incoming) return false;
  Frame item;
  while (xQueueReceive(incoming, &item, 0) == pdTRUE) {
    const bool valid = authenticated && item.epoch == wireEpoch.load();
    if (valid) frame = item.value;
    free(item.value); if (valid) return true;
  }
  return false;
}
}
