#include <M5Unified.h>
#include <ArduinoJson.h>
#include <esp_system.h>
#include <cstring>
#include <atomic>
#include "wifi_transport.h"
#include "body_led.h"

// Terminal only: fan policy and Hub ownership remain on the PC.
namespace {
constexpr uint32_t kStaleMs = 3000;
constexpr uint32_t kHoldMs = 650;
constexpr size_t kFrameBytes = 4096;
constexpr uint16_t BG = 0x10A3, FG = 0xEF7F, MUTED = 0xAD55, ACCENT = 0x9BBF, WARN = 0xFDC8;
enum class Screen { Home, Auto, Manual, Lighting, Waiting, Pending, Result, Motherboard, LightMenu, LightGroups, LightScenes, LightBrightness };
Screen screen = Screen::Waiting;
enum class Link { Usb, Wifi };
Link activeLink = Link::Usb;
std::atomic<bool> serialCorrupt{false};
M5Canvas canvas(&M5.Display);
JsonDocument state;
JsonDocument details;
JsonDocument motherboard;
JsonDocument lightingPanel, lightingCatalog;
bool lightingCapable = false, lightingResult = false;
bool bodyLedCapable = false, bodyLedAvailable = false, bodyLedEnabled = false;
BodyLed::Receiver bodyLed;
String bodyLedError;
uint32_t lastBodyDraw = 0;
uint64_t lightingSequence = 0;
uint32_t lightingReceived = 0, catalogRequestNumber = 0;
String lightingScope = "all", lightingEditStamp, catalogRequestId;
int lightSelection = 0, catalogSelection = 0, catalogTotal = 0, catalogPage = 0, draftBrightness = 100;
const char* lightMenuNames[] = {"対象グループ", "シーン", "明るさ", "消灯", "元に戻す", "同期停止", "NZXTプリセット", "本体LED同期"};
int lightMenuCount() { return bodyLedCapable ? 8 : 7; }
bool lightScreen() { return screen == Screen::LightMenu || screen == Screen::LightGroups || screen == Screen::LightScenes || screen == Screen::LightBrightness; }
uint64_t boardSequence = 0;
uint32_t boardReceived = 0;
int boardPage = 0;
String session, epoch, deviceId, bootId, pendingId, message, notice;
char frame[kFrameBytes];
size_t frameLength = 0;
bool dropping = false, connected = false, dirty = true, ackNeeded = false;
bool displayReady = false, fullColorBuffer = false;
bool heldB = false, bTracking = false, layoutB = false;
uint32_t lastReceive = 0, stateReceived = 0, lastHeartbeat = 0, lastHello = 0, pendingSince = 0, bDown = 0, lastDraw = 0;
uint32_t noticeSince = 0;
uint64_t snapshotSequence = 0, commandSequence = 0, editRevision = 0;
uint64_t editProfileRevisions[3] = {};
int draftDuty = 0, selection = 0, detailPage = 0;
const char* lightingIds[] = {"blue", "white", "purple", "off"};
const char* lightingNames[] = {"ブルー", "ホワイト", "パープル", "消灯"};
constexpr uint16_t lightingColors[] = {0x345F, 0xEF9F, 0xA3DD, 0x2946};

uint32_t elapsed(uint32_t since) { return static_cast<uint32_t>(millis() - since); }
bool linkAlive() { return connected && elapsed(lastReceive) < kStaleMs; }
bool telemetryFresh() {
  return linkAlive() && state["telemetryAgeMs"].is<double>() &&
    elapsed(stateReceived) + state["telemetryAgeMs"].as<double>() < kStaleMs;
}
bool hubReady() { return telemetryFresh() && state["hub"]["state"] == "ready"; }
bool faulted() { return state["recovery"]["faulted"] | false; }
bool canResume() { return hubReady() && (state["recovery"]["canResume"] | false); }
uint64_t revision(bool lighting = false) { return state[lighting ? "lightingRevision" : "controlRevision"].as<uint64_t>(); }
int minimum() { return (state["fan"]["allowStop"] | false) ? 0 : (state["fan"]["minimum"] | 1); }

void sendTo(JsonDocument& document, Link target) {
  if (measureJson(document) + 1 > kFrameBytes) return;
  if (target == Link::Wifi) { String json; serializeJson(document, json); WifiPanel::send(json); }
  else { serializeJson(document, Serial); Serial.write('\n'); }
}
void send(JsonDocument& document) { sendTo(document, activeLink); }
void envelope(JsonDocument& document, const char* type) {
  document["v"] = 1; document["type"] = type;
  document["sessionId"] = session; document["controllerEpoch"] = epoch;
}
void hello(Link target = Link::Usb) {
  JsonDocument document; document["v"] = 1; document["type"] = "hello";
  document["deviceType"] = "M5GO"; document["deviceId"] = deviceId;
  document["bootId"] = bootId; document["firmwareVersion"] = "1.4.0";
  document["capabilities"].to<JsonArray>().add("motherboard-v1");
  document["capabilities"].as<JsonArray>().add("lighting-v1");
  if (bodyLedAvailable) document["capabilities"].as<JsonArray>().add("body-led-v1");
  // Report allocation success: a returned pushSprite call alone is not evidence of rendering.
  auto display = document["display"].to<JsonObject>();
  display["ready"] = displayReady; display["board"] = static_cast<int>(M5.getBoard());
  display["width"] = M5.Display.width(); display["height"] = M5.Display.height();
  display["canvasBytes"] = canvas.bufferLength(); display["colorBits"] = canvas.getColorDepth();
  display["fullColorBuffer"] = fullColorBuffer; display["freeHeap"] = ESP.getFreeHeap();
  WifiPanel::status(document, target == Link::Wifi ? "wifi" : "usb");
  sendTo(document, target); lastHello = millis();
}
void disconnect() {
  bodyLed.reset(); bodyLedEnabled = bodyLedCapable = false; BodyLed::write({}); bodyLedError = "";
  connected = false; session = ""; epoch = ""; state.clear(); details.clear(); motherboard.clear(); boardSequence = 0;
  screen = Screen::Waiting; pendingId = ""; message = "PCとの通信が切れました";
  notice = "";
  ackNeeded = false; snapshotSequence = 0; commandSequence = 0; dirty = true;
  lightingPanel.clear(); lightingCatalog.clear(); lightingSequence = 0; lightingCapable = false;
  lightingScope = "all"; lightingEditStamp = ""; catalogRequestId = ""; lightingResult = false;
}
void queryResult() {
  if (!linkAlive() || pendingId.isEmpty()) return;
  JsonDocument document; envelope(document, "getResult"); document["id"] = pendingId; send(document);
  pendingSince = millis(); // Only query, never resend the command.
}
void submit(const char* command) {
  if (!displayReady || !hubReady() || screen == Screen::Pending) return;
  lightingResult = !strcmp(command, "SetLighting");
  JsonDocument document; envelope(document, "command");
  commandSequence++;
  pendingId = bootId + "-" + String(static_cast<unsigned long>(commandSequence));
  document["id"] = pendingId; document["commandSequence"] = commandSequence;
  document["expectedRevision"] = !strcmp(command, "ResumeControl") ? revision() : editRevision;
  document["command"] = command;
  auto payload = document["payload"].to<JsonObject>();
  if (!strcmp(command, "SetManualDuty")) payload["duty"] = draftDuty;
  if (!strcmp(command, "SelectAutoProfile")) {
    payload["profileId"] = state["profiles"][selection]["id"];
    payload["profileRevision"] = editProfileRevisions[selection];
  }
  if (!strcmp(command, "SetLighting")) payload["preset"] = lightingIds[selection];
  screen = Screen::Pending; pendingSince = millis(); dirty = true; send(document);
}

bool lightingFresh() { return linkAlive() && lightingCapable && lightingSequence > 0 && elapsed(lightingReceived) < kStaleMs && lightingPanel["scopeId"] == lightingScope; }
void requestLightingState() {
  JsonDocument document; envelope(document, "getLightingState"); document["scopeId"] = lightingScope; send(document);
}
void openLightMenu() {
  screen = Screen::LightMenu; lightSelection = 0; lightingEditStamp = ""; catalogRequestId = "";
  requestLightingState(); dirty = true;
}
void requestCatalog() {
  catalogPage = catalogSelection / 8; lightingCatalog.clear();
  catalogRequestId = String(++catalogRequestNumber);
  JsonDocument document; envelope(document, "getLightingCatalog");
  document["scopeId"] = lightingScope; document["kind"] = screen == Screen::LightGroups ? "groups" : "scenes";
  document["page"] = catalogPage; document["requestId"] = catalogRequestId; send(document); dirty = true;
}
void submitLighting(const char* command, const String& sceneId = "") {
  if (!displayReady || !lightingFresh() || screen == Screen::Pending) return;
  lightingResult = true;
  if (!(lightingPanel["ready"] | false) || (lightingPanel["busy"] | false) || lightingPanel["stamp"] != lightingEditStamp) {
    message = lightingPanel["error"].is<const char*>() ? lightingPanel["error"].as<String>() : "設定が変更されました。照明を開き直してください";
    screen = Screen::Result; details.clear(); detailPage = 0; dirty = true; return;
  }
  JsonDocument document; envelope(document, "command");
  pendingId = bootId + "-" + String(static_cast<unsigned long>(++commandSequence));
  document["id"] = pendingId; document["commandSequence"] = commandSequence; document["expectedRevision"] = 0;
  document["command"] = command;
  auto payload = document["payload"].to<JsonObject>(); payload["scopeId"] = lightingScope; payload["expectedStamp"] = lightingEditStamp;
  if (!strcmp(command, "ApplyLightingScene")) payload["sceneId"] = sceneId;
  if (!strcmp(command, "SetLightingBrightness")) payload["brightness"] = draftBrightness;
  screen = Screen::Pending; pendingSince = millis(); dirty = true; send(document);
}
bool boundedString(JsonVariantConst v, size_t limit) { return v.is<const char*>() && strlen(v.as<const char*>()) <= limit; }
bool hexId(JsonVariantConst v, size_t length) {
  if (!boundedString(v, length) || strlen(v.as<const char*>()) != length) return false;
  for (size_t i = 0; i < length; i++) { const char c = v.as<const char*>()[i]; if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false; }
  return true;
}

bool validState(JsonDocument& document) {
  if (!document["snapshotSequence"].is<uint64_t>() || !document["controlRevision"].is<uint64_t>() || !document["lightingRevision"].is<uint64_t>()) return false;
  if (!document["fan"].is<JsonObject>() || !document["hub"].is<JsonObject>() || !document["recovery"].is<JsonObject>() || !document["temperature"].is<JsonObject>()) return false;
  if (!document["fan"]["channels"].is<JsonArray>() || document["fan"]["channels"].size() > 5 || !document["profiles"].is<JsonArray>() || document["profiles"].size() != 3) return false;
  if (!document["fan"]["confirmedDuty"].isNull() && (!document["fan"]["confirmedDuty"].is<int>() || document["fan"]["confirmedDuty"].as<int>() < 0 || document["fan"]["confirmedDuty"].as<int>() > 100)) return false;
  return true;
}
void receiveLine(const char* bytes, size_t length, Link source) {
  JsonDocument document;
  if (deserializeJson(document, bytes, length) || document["v"] != 1 || !document["type"].is<const char*>()) return;
  const char* type = document["type"];
  if (source == Link::Usb && !strcmp(type, "identify")) { hello(Link::Usb); return; }
  if (source == Link::Usb && !strcmp(type, "configureWifi")) {
    if (document["deviceId"] != deviceId || !document["id"].is<const char*>() || strlen(document["id"]) > 64) return;
    String error; const bool ok = WifiPanel::configure(document["payload"].as<JsonVariantConst>(), error);
    JsonDocument response; response["v"] = 1; response["type"] = "wifiConfigured";
    response["deviceId"] = deviceId; response["id"] = document["id"]; response["ok"] = ok;
    if (!ok) response["error"] = error;
    sendTo(response, Link::Usb); return;
  }
  if (!strcmp(type, "helloRequest")) { hello(source); return; }
  if (!strcmp(type, "welcome")) {
    if (source == Link::Wifi && activeLink == Link::Usb && linkAlive()) return;
    if (!document["sessionId"].is<const char*>() || !document["controllerEpoch"].is<const char*>() || document["protocol"] != 1) return;
    String nextSession = document["sessionId"].as<String>();
    String nextEpoch = document["controllerEpoch"].as<String>();
    if (nextSession.isEmpty() || nextEpoch.isEmpty() || nextSession.length() > 64 || nextEpoch.length() > 64) return;
    if (session != nextSession || epoch != nextEpoch || activeLink != source) {
      bodyLed.reset(); bodyLedEnabled = false; BodyLed::write({}); bodyLedError = "";
      state.clear(); pendingId = ""; details.clear(); screen = Screen::Waiting; snapshotSequence = 0; commandSequence = 0;
      motherboard.clear(); boardSequence = 0; boardPage = 0;
      lightingPanel.clear(); lightingCatalog.clear(); lightingSequence = 0; lightingScope = "all";
      lightingEditStamp = ""; catalogRequestId = ""; lightingResult = false;
    }
    lightingCapable = bodyLedCapable = false;
    for (JsonVariantConst capability : document["capabilities"].as<JsonArrayConst>()) {
      if (capability == "lighting-v1") lightingCapable = true;
      if (capability == "body-led-v1" && bodyLedAvailable) bodyLedCapable = true;
    }
    bodyLedCapable = bodyLedCapable && lightingCapable;
    if (!bodyLedCapable) { bodyLed.reset(); bodyLedEnabled = false; BodyLed::write({}); }
    activeLink = source; session = nextSession; epoch = nextEpoch; layoutB = document["layout"] == "B";
    connected = true; lastReceive = millis(); dirty = true; return;
  }
  if (source != activeLink || !linkAlive() || document["sessionId"] != session || document["controllerEpoch"] != epoch) return;
  if (!strcmp(type, "bodyLedState")) {
    if (!bodyLedCapable || (!document["error"].isNull() && !boundedString(document["error"], 300)) ||
        !bodyLed.accept(document.as<JsonVariantConst>(), lightingScope.c_str(), millis())) return;
    bodyLedError = document["error"] | ""; dirty = true;
  } else if (!strcmp(type, "lightingState")) {
    if (!lightingCapable || !document["lightingSequence"].is<uint64_t>() || document["lightingSequence"].as<uint64_t>() <= lightingSequence ||
        document["scopeId"] != lightingScope || !hexId(document["stamp"], 64) || !boundedString(document["scopeName"], 160) ||
        !document["ready"].is<bool>() || !document["busy"].is<bool>() || !document["off"].is<bool>() ||
        !document["canRestore"].is<bool>() || !document["synchronized"].is<bool>() || !document["brightness"].is<int>() ||
        document["brightness"].as<int>() < 1 || document["brightness"].as<int>() > 100 ||
        (!document["error"].isNull() && !boundedString(document["error"], 400))) return;
    lightingPanel = document; lightingSequence = document["lightingSequence"].as<uint64_t>(); lightingReceived = millis();
    if (screen == Screen::LightMenu && lightingEditStamp.isEmpty() && !(document["busy"] | false)) lightingEditStamp = document["stamp"].as<String>();
    dirty = true;
  } else if (!strcmp(type, "lightingCatalog")) {
    if (!lightingCapable || (screen != Screen::LightGroups && screen != Screen::LightScenes) || catalogRequestId.isEmpty() ||
        document["requestId"] != catalogRequestId || document["scopeId"] != lightingScope ||
        document["kind"] != (screen == Screen::LightGroups ? "groups" : "scenes") || !document["page"].is<int>() || document["page"].as<int>() != catalogPage ||
        !document["total"].is<int>() || document["total"].as<int>() < 0 || document["total"].as<int>() > (screen == Screen::LightGroups ? 33 : 32) ||
        !hexId(document["stamp"], 64) || !document["items"].is<JsonArray>() || document["items"].size() > 8) return;
    for (JsonVariantConst item : document["items"].as<JsonArrayConst>())
      if (!(screen == Screen::LightGroups && item["id"] == "all") && !hexId(item["id"], 32)) return;
      else if (!boundedString(item["name"], 160)) return;
    catalogTotal = document["total"].as<int>();
    if (catalogSelection >= catalogTotal && catalogTotal > 0) { catalogSelection = 0; requestCatalog(); return; }
    if (document["items"].size() != static_cast<size_t>(max(0, min(8, catalogTotal - catalogPage * 8)))) return;
    lightingCatalog = document; lightingEditStamp = document["stamp"].as<String>(); dirty = true;
  } else if (!strcmp(type, "motherboardState")) {
    if (!document["boardSequence"].is<uint64_t>() || !document["rows"].is<JsonArray>() || document["rows"].size() > 8 ||
        !document["name"].is<const char*>() || !document["status"].is<const char*>() || !document["fresh"].is<bool>()) return;
    if (strlen(document["name"]) > 96 || strlen(document["status"]) > 120) return;
    if (!document["ageMs"].isNull() && (!document["ageMs"].is<double>() || !isfinite(document["ageMs"].as<double>()) || document["ageMs"].as<double>() < 0)) return;
    const uint64_t next = document["boardSequence"].as<uint64_t>();
    if (next <= boardSequence) return;
    for (JsonVariantConst row : document["rows"].as<JsonArrayConst>()) {
      if (!row["name"].is<const char*>() || strlen(row["name"]) > 72 || !row["unit"].is<const char*>() || strlen(row["unit"]) > 8 ||
          !row["kind"].is<const char*>() || strlen(row["kind"]) > 16 ||
          (!row["value"].isNull() && (!row["value"].is<double>() || !isfinite(row["value"].as<double>())))) return;
    }
    motherboard = document; boardSequence = next; boardReceived = millis(); dirty = true;
  } else if (!strcmp(type, "state")) {
    if (!validState(document)) return;
    const uint64_t next = document["snapshotSequence"].as<uint64_t>();
    if (next < snapshotSequence) return;
    // Repeated snapshots do not rejuvenate old telemetry.
    if (next == snapshotSequence && !state.isNull()) return;
    state = document; snapshotSequence = next; stateReceived = millis();
    ackNeeded = true; layoutB = document["layout"] == "B";
    if (screen == Screen::Waiting) screen = Screen::Home;
    if (!hubReady() && !lightScreen() && screen != Screen::Pending && screen != Screen::Result && screen != Screen::Motherboard) screen = Screen::Home;
    dirty = true;
  } else if (!strcmp(type, "accepted")) {
    if (document["id"] != pendingId) return;
    screen = Screen::Pending; dirty = true;
  } else if (!strcmp(type, "result")) {
    if (document["id"] != pendingId || !document["outcome"].is<const char*>()) return;
    const char* outcome = document["outcome"];
    message = document["message"] | "結果を確認できません";
    details = document; detailPage = 0;
    if (!strcmp(outcome, "confirmed") || !strcmp(outcome, "writtenUnverified")) {
      screen = Screen::Home; noticeSince = millis();
      notice = !strcmp(outcome, "writtenUnverified") ? "照明:送信済み" : "全対象の出力を確認";
    }
    else screen = Screen::Result;
    pendingId = ""; dirty = true;
  } else if (strcmp(type, "heartbeat")) return;
  lastReceive = millis();
}

void readSerial() {
  if (serialCorrupt.exchange(false)) { dropping = true; frameLength = 0; }
  // Bound work per loop so a noisy link cannot starve buttons / stale detection.
  size_t budget = 512;
  while (budget-- && Serial.available()) {
    char c = Serial.read();
    if (c == '\n') {
      if (!dropping && frameLength < kFrameBytes) { frame[frameLength] = 0; receiveLine(frame, frameLength, Link::Usb); }
      dropping = false; frameLength = 0;
    } else if (!dropping) {
      if (frameLength >= kFrameBytes - 1) { dropping = true; frameLength = 0; }
      else frame[frameLength++] = c;
    }
  }
}
void move(int delta) {
  if (lightScreen()) {
    if (screen == Screen::LightMenu) lightSelection = (lightSelection + delta + lightMenuCount()) % lightMenuCount();
    else if (screen == Screen::LightBrightness) draftBrightness = constrain(draftBrightness + delta * 5, 1, 100);
    else if (!lightingCatalog.isNull() && catalogTotal > 0) {
      catalogSelection = (catalogSelection + delta + catalogTotal) % catalogTotal;
      if (catalogSelection / 8 != catalogPage) requestCatalog();
    }
    dirty = true; return;
  }
  if (screen == Screen::Motherboard) {
    const int pages = max(1, static_cast<int>((motherboard["rows"].size() + 3) / 4));
    boardPage = (boardPage + delta + pages) % pages; dirty = true; return;
  }
  if (screen == Screen::Result) {
    if (lightingResult) return;
    const int count = 1 + (details["channels"].is<JsonArray>() ? details["channels"].size() : state["fan"]["channels"].size());
    detailPage = (detailPage + delta + count) % count; dirty = true; return;
  }
  if (!hubReady()) return;
  if (screen == Screen::Manual) draftDuty = constrain(draftDuty + delta * 5, minimum(), 100);
  else if (screen == Screen::Auto) selection = (selection + delta + 3) % 3;
  else if (screen == Screen::Lighting) selection = (selection + delta + 4) % 4;
  dirty = true;
}
void beginEdit(Screen next) {
  if (!displayReady) return;
  if (next == Screen::Lighting && lightingCapable && linkAlive()) { openLightMenu(); return; }
  lightingResult = next == Screen::Lighting;
  if (linkAlive() && faulted() && next != Screen::Lighting) {
    details.clear(); detailPage = 0; screen = Screen::Result; message = state["recovery"]["reason"] | "異常詳細"; dirty = true; return;
  }
  if (!hubReady()) return;
  if (next != Screen::Lighting && (!(state["fan"]["ready"] | false) || (state["fan"]["busy"] | false))) return;
  if (next == Screen::Lighting && (!(state["lighting"]["ready"] | false) || (state["lighting"]["busy"] | false))) return;
  screen = next; editRevision = revision(next == Screen::Lighting); selection = 0;
  if (next == Screen::Lighting) for (int i = 0; i < 4; i++) if (state["lighting"]["preset"] == lightingIds[i]) selection = i;
  draftDuty = state["fan"]["confirmedDuty"] | minimum();
  for (int i = 0; i < 3; i++) editProfileRevisions[i] = state["profiles"][i]["profileRevision"].as<uint64_t>();
  dirty = true;
}
void shortB() {
  if (!linkAlive()) return;
  switch (screen) {
    case Screen::LightMenu:
      if (lightSelection == 0 || lightSelection == 1) {
        screen = lightSelection == 0 ? Screen::LightGroups : Screen::LightScenes;
        catalogSelection = 0; catalogTotal = 0; requestCatalog();
      } else if (lightSelection == 2 && lightingFresh()) {
        draftBrightness = lightingPanel["brightness"] | 100; screen = Screen::LightBrightness;
      } else if (lightSelection == 3) submitLighting("LightingOff");
      else if (lightSelection == 4 && (lightingPanel["canRestore"] | false)) submitLighting("RestoreLighting");
      else if (lightSelection == 5 && (lightingPanel["synchronized"] | false)) submitLighting("StopLightingSync");
      else if (lightSelection == 6 && hubReady()) { screen = Screen::Lighting; selection = 0; editRevision = revision(true); }
      else if (lightSelection == 7 && bodyLedCapable) { bodyLedEnabled = !bodyLedEnabled; if (!bodyLedEnabled) BodyLed::write({}); }
      break;
    case Screen::LightGroups:
      if (!lightingCatalog.isNull() && catalogTotal > 0) {
        bodyLed.clear(); BodyLed::write({}); bodyLedError = "";
        lightingScope = lightingCatalog["items"][catalogSelection % 8]["id"].as<String>(); openLightMenu();
      }
      break;
    case Screen::LightScenes:
      if (!lightingCatalog.isNull() && catalogTotal > 0) submitLighting("ApplyLightingScene", lightingCatalog["items"][catalogSelection % 8]["id"].as<String>());
      break;
    case Screen::LightBrightness: submitLighting("SetLightingBrightness"); break;
    case Screen::Home: beginEdit(Screen::Manual); break;
    case Screen::Motherboard: screen = Screen::Home; break;
    case Screen::Manual: submit("SetManualDuty"); break;
    case Screen::Auto: if (state["profiles"][selection]["ready"] | false) submit("SelectAutoProfile"); break;
    case Screen::Lighting: submit("SetLighting"); break;
    case Screen::Result:
      if (lightingResult) { if (lightingCapable) openLightMenu(); else screen = Screen::Home; }
      else if (faulted()) { if (canResume()) submit("ResumeControl"); }
      else { screen = Screen::Home; JsonDocument document; envelope(document, "getState"); send(document); }
      break;
    default: break;
  }
  dirty = true;
}
void longB() {
  if (screen == Screen::Pending || screen == Screen::Waiting) return;
  screen = screen == Screen::Home ? Screen::Motherboard : Screen::Home;
  catalogRequestId = ""; lightingEditStamp = "";
  boardPage = 0; dirty = true;
}
void buttons() {
  if (M5.BtnA.wasPressed()) { if (screen == Screen::Home) beginEdit(Screen::Auto); else move(-1); }
  if (M5.BtnC.wasPressed()) { if (screen == Screen::Home) beginEdit(Screen::Lighting); else move(1); }
  if (M5.BtnB.wasPressed()) { bTracking = true; heldB = false; bDown = millis(); }
  if (bTracking && M5.BtnB.isPressed() && !heldB && elapsed(bDown) >= kHoldMs) {
    heldB = true;
    longB();
  }
  if (M5.BtnB.wasReleased()) {
    // A slow draw may span the hold threshold and release. Never turn that hold into Apply.
    if (bTracking && !heldB && elapsed(bDown) >= kHoldMs) {
      heldB = true;
      longB();
    }
    if (bTracking && !heldB) shortB();
    bTracking = false;
  }
}
void text(const String& value, int x, int y, uint16_t color = FG, int size = 12) {
  canvas.setFont(size >= 16 ? &fonts::efontJA_16 : &fonts::efontJA_12);
  canvas.setTextColor(color, BG); canvas.setTextSize(1); canvas.drawString(value, x, y);
}
void footer(const char* a, const char* b, const char* c) {
  canvas.drawFastHLine(0, 208, 320, MUTED);
  text(a, 10, 218); text(b, 115, 218); text(c, 241, 218);
}
String fitLabel(String value, int width, int size = 12) {
  canvas.setFont(size >= 16 ? &fonts::efontJA_16 : &fonts::efontJA_12);
  while (canvas.textWidth(value) > width && value.length()) {
    int cut = value.length() - 1;
    while (cut > 0 && (static_cast<uint8_t>(value[cut]) & 0xC0) == 0x80) cut--;
    value.remove(cut);
  }
  return value;
}
void drawLighting() {
  text(screen == Screen::LightMenu ? "照明" : screen == Screen::LightGroups ? "対象グループ" : screen == Screen::LightScenes ? "シーン" : "明るさ", 10, 36, FG, 16);
  if (screen != Screen::LightBrightness) {
    const int index = screen == Screen::LightMenu ? lightSelection : catalogSelection;
    const int total = screen == Screen::LightMenu ? lightMenuCount() : catalogTotal;
    text(String(index / 4 + 1) + "/" + String(max(1, (total + 3) / 4)), 270, 38, MUTED);
  }
  text(fitLabel(lightingPanel["scopeId"] == lightingScope ? lightingPanel["scopeName"].as<String>() : "対象を取得中", 298), 10, 59, MUTED);
  if (screen == Screen::LightBrightness) {
    canvas.setFont(&fonts::Font7); canvas.setTextColor(FG, BG); canvas.drawString(String(draftBrightness), 88, 89);
    text("%", 222, 119, FG, 16);
    canvas.drawRect(15, 153, 290, 8, MUTED); canvas.fillRect(16, 154, draftBrightness * 288 / 100, 6, ACCENT);
    text("設定値 / Bで適用", 15, 169, MUTED); footer("−5%", "適用", "＋5%");
  } else {
    const bool menu = screen == Screen::LightMenu;
    const int selected = menu ? lightSelection : catalogSelection;
    const int first = selected / 4 * 4, count = menu ? lightMenuCount() : catalogTotal;
    if (!menu && lightingCatalog.isNull()) text("一覧を取得しています", 14, 108, MUTED);
    else if (!menu && !count) text("PCでシーンを保存してください", 14, 108, MUTED);
    else for (int i = first; i < min(first + 4, count); i++) {
      const int y = 83 + (i - first) * 25;
      if (selected == i) canvas.fillRoundRect(8, y - 3, 304, 24, 4, 0x394B);
      String label = menu ? lightMenuNames[i] : lightingCatalog["items"][i % 8]["name"].as<String>();
      if (menu && i == 2) label += "  " + String(lightingPanel["brightness"] | 100) + "%";
      if (menu && i == 4 && !(lightingPanel["canRestore"] | false)) label += "（なし）";
      if (menu && i == 5 && !(lightingPanel["synchronized"] | false)) label += "（停止中）";
      if (menu && i == 7) label += bodyLedEnabled ? (bodyLed.fresh(millis()) ? " ON" : " 待機") : " OFF";
      text((selected == i ? "▶ " : "   ") + fitLabel(label, 266, 16), 12, y, FG, 16);
    }
    footer("前へ", screen == Screen::LightScenes ? "適用" : "決定", "次へ");
  }
  const String hint = screen == Screen::LightMenu && lightSelection == 7 && bodyLedEnabled && !bodyLed.fresh(millis()) ?
    (bodyLedError.isEmpty() ? "照明の更新待ちです" : bodyLedError) : !lightingFresh() ? "更新待ち" : (lightingPanel["busy"] | false) ? "PCで処理中" :
    lightingPanel["stamp"] != lightingEditStamp ? "設定変更あり・開き直してください" : "B長押しで破棄・ホーム";
  text(fitLabel(hint, 300), 10, 192, WARN);
}
String number(JsonVariantConst value) { return value.isNull() ? "—" : String(value.as<int>()); }
String temperature(const char* id) {
  auto t = state["temperature"][id];
  if (!linkAlive() || !(t["valid"] | false) || t["celsius"].isNull() || t["ageMs"].isNull() ||
      elapsed(stateReceived) + t["ageMs"].as<double>() >= kStaleMs) return "—";
  return String(t["celsius"].as<float>(), 0);
}
String modeName() {
  String mode = state["fan"]["mode"] | "monitor";
  if (mode == "manual") return "手動";
  if (mode == "fallback") return "異常時制御";
  if (mode == "unknown") return "状態未確認";
  if (mode == "auto") {
    for (auto p : state["profiles"].as<JsonArray>()) if (p["id"] == state["fan"]["profileId"]) return "自動・" + p["name"].as<String>();
    return "自動";
  }
  return "監視のみ";
}
void home() {
  String duty = telemetryFresh() ? number(state["fan"]["confirmedDuty"]) : "—";
  if (layoutB) {
    text("CPU " + temperature("cpu") + "°C", 10, 36); text("GPU " + temperature("gpu") + "°C", 202, 36);
    text(modeName(), 10, 65);
    if (duty == "—") text(duty, 143, 107, MUTED, 16);
    else { canvas.setFont(&fonts::Font7); canvas.setTextColor(FG, BG); canvas.drawString(duty, 111, 90); }
    text("%", 231, 116, FG, 16);
    for (int i = 0; i < 5; i++) { auto c = state["fan"]["channels"][i]; text("F" + String(i+1) + " " + (telemetryFresh() ? number(c["rpm"]) : "—"), 10 + (i % 3) * 101, 151 + (i / 3) * 20); }
    canvas.drawRect(10, 194, 300, 6, MUTED);
    if (telemetryFresh() && !state["fan"]["confirmedDuty"].isNull()) canvas.fillRect(11, 195, state["fan"]["confirmedDuty"].as<int>() * 298 / 100, 4, ACCENT);
  } else {
    text("共通出力", 10, 33); text(modeName(), 173, 33);
    if (duty == "—") text("— %", 10, 58, MUTED, 16);
    else { canvas.setFont(&fonts::Font4); canvas.setTextColor(FG, BG); canvas.drawString(duty + " %", 10, 53); }
    text("CPU " + temperature("cpu") + "°C", 137, 68); text("GPU " + temperature("gpu") + "°C", 231, 68);
    text("チャンネル", 10, 91, MUTED); text("rpm", 159, 91, MUTED); text("状態", 251, 91, MUTED);
    for (int i = 0; i < 5; i++) {
      auto c = state["fan"]["channels"][i]; const int y = 110 + i * 20;
      text("FAN " + String(i + 1), 10, y); text(telemetryFresh() ? number(c["rpm"]) : "—", 147, y);
      String condition = !telemetryFresh() ? "未確認" : c["presence"] == "absent" ? "未接続" : c["rpm"].isNull() ? "未確認" : c["rpm"].as<int>() == 0 ? "停止" : "回転中";
      text(condition, 245, y, condition == "未確認" ? WARN : FG);
    }
  }
  footer("自動モード", faulted() ? "異常詳細" : "手動出力", "照明");
}
void draw() {
  if (!displayReady) return;
  canvas.fillScreen(BG); canvas.fillRect(0, 0, 320, 28, 0x2947);
  text(!notice.isEmpty() && elapsed(noticeSince) < 3000 ? notice : activeLink == Link::Wifi ? "FlowDeck / Wi-Fi " + WifiPanel::signal() : "FlowDeck / USB", 9, 7);
  text(hubReady() ? "接続済み" : linkAlive() ? "Hub未確認" : "PC未接続", 241, 7, hubReady() ? FG : WARN);
  if (!linkAlive() || screen == Screen::Waiting) {
    text(connected ? "状態を確認しています" : message.isEmpty() ? "PCへの接続を待っています" : message, 25, 91, FG, 16);
    text("現在の値は確認できません", 53, 133, MUTED);
    if (WifiPanel::configured()) text(WifiPanel::label(), 20, 171, MUTED);
    footer("—", "—", "—");
  } else if (screen == Screen::Motherboard) {
    const bool fresh = boardSequence > 0 && (motherboard["fresh"] | false) && motherboard["ageMs"].is<double>() &&
      elapsed(boardReceived) + motherboard["ageMs"].as<double>() < kStaleMs;
    text("マザーボード  " + String(boardPage + 1) + "/" + String(max(1, static_cast<int>((motherboard["rows"].size() + 3) / 4))), 10, 36, FG, 16);
    text(boardSequence == 0 ? "PCからの情報を待っています" : !fresh ? "更新停止・値は未確認" : motherboard["status"].as<String>(), 10, 60, fresh ? MUTED : WARN);
    if (boardSequence > 0 && motherboard["rows"].size() == 0) text("PCでM5表示項目を選んで保存", 10, 105);
    for (int i = 0; i < 4; i++) {
      const int index = boardPage * 4 + i;
      if (index >= static_cast<int>(motherboard["rows"].size())) break;
      auto row = motherboard["rows"][index];
      const int y = 87 + i * 29;
      String label = row["name"].as<String>();
      // Measure glyph width: 18 Japanese characters can exceed the available label column.
      canvas.setFont(&fonts::efontJA_12);
      while (canvas.textWidth(label) > 178 && label.length()) {
        int cut = label.length() - 1;
        while (cut > 0 && (static_cast<uint8_t>(label[cut]) & 0xC0) == 0x80) cut--;
        label.remove(cut);
      }
      text(label, 10, y);
      String value = !fresh || row["value"].isNull() ? "—" : String(row["value"].as<double>(), row["kind"] == "fan" ? 0 : row["kind"] == "voltage" ? 2 : 1);
      text(value + " " + row["unit"].as<String>(), 198, y);
    }
    text("B長押しでもホームへ戻れます", 10, 192, MUTED);
    footer("前のページ", "ホーム", "次のページ");
  } else if (lightScreen()) drawLighting();
  else if (!hubReady() && screen != Screen::Result && screen != Screen::Pending) {
    text(state["hub"]["state"] == "disconnected" ? "Control Hubに接続できません" : "状態を確認しています", 12, 88, FG, 16);
    text(faulted() ? "復旧待ち・Bで異常詳細" : "古い温度・RPMは表示しません", 38, 130, MUTED);
    footer("—", faulted() ? "異常詳細" : "—", lightingCapable ? "照明" : "—");
  } else if (screen == Screen::Home) home();
  else if (screen == Screen::Manual) {
    text("全ファン・手動出力", 12, 39, FG, 16); text("現在 " + modeName() + " / " + number(state["fan"]["confirmedDuty"]) + "%", 12, 68);
    canvas.setFont(&fonts::Font7); canvas.setTextColor(FG, BG); canvas.drawString(String(draftDuty), 96, 97); text("%", 219, 129, FG, 16);
    text(editRevision != revision() ? "PC側で変更あり" : "変更案・B長押しでキャンセル", 20, 177, WARN); footer("−5%", "一括適用", "＋5%");
  } else if (screen == Screen::Auto || screen == Screen::Lighting) {
    const bool rgb = screen == Screen::Lighting; text(rgb ? "RGB・全チャンネル" : "全ファン・自動モード", 12, 38, FG, 16);
    if (rgb) {
      String last = "送信履歴なし";
      for (int i = 0; i < 4; i++) if (state["lighting"]["preset"] == lightingIds[i]) last = lightingNames[i];
      text((state["lighting"]["previousConnection"] | false) ? "前回接続時: " + last : "最後の送信: " + last, 12, 58, MUTED);
    }
    const int count = rgb ? 4 : 3;
    for (int i = 0; i < count; i++) {
      const int y = rgb ? 82 + i * 24 : 68 + i * 29;
      if (selection == i) canvas.fillRoundRect(8, y - 3, 304, rgb ? 23 : 26, 4, 0x394B);
      String label = rgb ? lightingNames[i] : state["profiles"][i]["name"].as<String>();
      if (!rgb && !(state["profiles"][i]["ready"] | false)) label += "（未設定）";
      text((selection == i ? "▶ " : "   ") + label, 14, y, FG, 16);
      if (rgb) { canvas.fillRoundRect(270, y, 29, 17, 3, lightingColors[i]); canvas.drawRoundRect(270, y, 29, 17, 3, MUTED); }
    }
    text(editRevision != revision(rgb) ? "PC側で変更あり" : "B長押しでキャンセル", 20, 191, WARN); footer("前へ", rgb ? "送信" : "適用", "次へ");
  } else if (screen == Screen::Pending) {
    text("適用中", 122, 92, FG, 16); text("処理結果を待っています", 72, 131, MUTED); footer("—", "—", "—");
  } else if (screen == Screen::Result) {
    text(lightingResult ? "照明の適用結果" : "適用結果 / 異常詳細", 12, 39, WARN, 16);
    if (detailPage == 0) {
      String reason = !lightingResult && faulted() ? state["recovery"]["reason"].as<String>() : message;
      canvas.setClipRect(10, 77, 300, 80);
      canvas.setFont(&fonts::efontJA_16); canvas.setTextColor(FG, BG); canvas.setCursor(10, 77); canvas.setTextWrap(true); canvas.print(reason);
      canvas.clearClipRect();
      text(lightingResult ? "Bで照明へ / 長押しでホーム" : canResume() ? "条件回復・Bで制御を再開" : faulted() ? "復旧条件の回復を待っています" : "Bで最新状態を確認", 10, 167, MUTED);
    } else {
      const bool historical = details["channels"].is<JsonArray>();
      JsonVariantConst c = historical ? details["channels"][detailPage-1].as<JsonVariantConst>() : state["fan"]["channels"][detailPage-1].as<JsonVariantConst>();
      String channel = c["id"] | "—"; channel.toUpperCase();
      text(channel + (historical ? " 最終処理結果" : " 最新報告"), 12, 77, FG, 16);
      if (historical) {
        text("送信: " + c["write"].as<String>(), 12, 111); text("報告: " + number(c["reportedDuty"]) + "%", 12, 138);
        text("結果: " + c["confirmation"].as<String>(), 12, 164);
      } else {
        text("出力: " + (telemetryFresh() ? number(c["duty"]) : "—") + "%", 12, 111);
        text("回転数: " + (telemetryFresh() ? number(c["rpm"]) : "—") + " rpm", 12, 138);
        text(telemetryFresh() ? "Hubからの報告値" : "状態が古いため未確認", 12, 164, MUTED);
      }
    }
    footer(lightingResult ? "—" : "前の詳細", lightingResult ? "照明へ" : canResume() ? "再開" : faulted() ? "復旧待ち" : "閉じる", lightingResult ? "—" : "次の詳細");
  }
  canvas.pushSprite(0, 0); dirty = false; lastDraw = millis();
  // Acknowledge only after the accepted snapshot has been rendered.
  if (ackNeeded && linkAlive()) {
    JsonDocument document; envelope(document, "stateApplied"); document["snapshotSequence"] = snapshotSequence;
    document["controlRevision"] = revision(); document["lightingRevision"] = revision(true); send(document); ackNeeded = false;
  }
}
} // namespace

void setup() {
  // M5.begin starts UART0; reserve enough room before it does so. A PEM pairing
  // frame must survive a display update without losing bytes inside its JSON string.
  Serial.setRxBufferSize(8192);
  auto config = M5.config(); config.serial_baudrate = 115200;
  M5.begin(config); M5.Display.setRotation(1); M5.Display.setBrightness(128);
  bodyLedAvailable = BodyLed::begin();
  Serial.begin(115200); Serial.setTimeout(10);
  Serial.onReceiveError([](hardwareSerial_error_t) { serialCorrupt = true; });
  canvas.setColorDepth(16);
  fullColorBuffer = canvas.createSprite(320, 240) != nullptr;
  if (!fullColorBuffer) {
    // Original M5GO models may have no PSRAM or no contiguous 154 KiB DMA block.
    // RGB332 halves the framebuffer and keeps all UI colors and Japanese text.
    canvas.setColorDepth(8);
    canvas.createSprite(320, 240);
  }
  displayReady = canvas.getBuffer() != nullptr && M5.getDisplayCount() > 0 && M5.Display.width() == 320 && M5.Display.height() == 240;
  if (!displayReady) {
    M5.Display.fillScreen(BG); M5.Display.setTextColor(FG, BG);
    M5.Display.setFont(&fonts::efontJA_16);
    M5.Display.drawString("画面を初期化できません", 8, 70);
    M5.Display.drawString("PCで接続状態を確認してください", 8, 100);
  }
  char id[20]; snprintf(id, sizeof(id), "%012llX", ESP.getEfuseMac()); deviceId = id;
  char boot[20]; snprintf(boot, sizeof(boot), "%08lX%08lX", static_cast<unsigned long>(esp_random()), static_cast<unsigned long>(esp_random())); bootId = boot;
  WifiPanel::begin(deviceId);
  hello(); draw();
}
void loop() {
  M5.update(); readSerial(); buttons();
  String networkFrame; int networkBudget = 4;
  while (networkBudget-- > 0 && WifiPanel::receive(networkFrame)) receiveLine(networkFrame.c_str(), networkFrame.length(), Link::Wifi);
  if (connected && (elapsed(lastReceive) >= kStaleMs || (activeLink == Link::Wifi && !WifiPanel::ready()))) disconnect();
  if (elapsed(lastBodyDraw) >= 25) {
    BodyLed::write(bodyLedEnabled && bodyLedCapable && linkAlive() ? bodyLed.sample(millis()) : BodyLed::Color{});
    lastBodyDraw = millis();
  }
  if (elapsed(lastHello) >= 1000) {
    // Both links advertise availability; only the PC's selected welcome takes ownership.
    if (!connected || activeLink != Link::Usb) hello(Link::Usb);
    if (WifiPanel::ready() && (!connected || activeLink != Link::Wifi)) hello(Link::Wifi);
    JsonDocument status; status["v"] = 1; status["type"] = "wifiStatus"; status["deviceId"] = deviceId;
    WifiPanel::status(status, activeLink == Link::Wifi ? "wifi" : "usb"); sendTo(status, Link::Usb);
    lastHello = millis();
  }
  if (linkAlive() && elapsed(lastHeartbeat) >= 1000) {
    JsonDocument document; envelope(document, "heartbeat"); WifiPanel::status(document, activeLink == Link::Wifi ? "wifi" : "usb");
    send(document); lastHeartbeat = millis();
  }
  if (screen == Screen::Pending && elapsed(pendingSince) >= 5000) queryResult();
  if (dirty || elapsed(lastDraw) >= 250) draw();
  delay(5);
}
