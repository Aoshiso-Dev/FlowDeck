#pragma once
#include <ArduinoJson.h>
#include <cmath>
#include <cstdint>
#include <cstring>

// No hardware access: the same parser/effect code is exercised by the host tests.
namespace BodyLed {
struct Color { int r = 0, g = 0, b = 0; };
inline int rounded(double value) { return static_cast<int>(std::nearbyint(value)); }
class Receiver {
  uint64_t sequence = 0;
  uint32_t received = 0, lifetime = 0;
  int effect = 0, brightness = 0, period = 0;
  double phase = 0;
  Color base;
 public:
  bool ready = false;
  void clear() { ready = false; lifetime = 0; }
  void reset() { clear(); sequence = 0; }
  bool accept(JsonVariantConst document, const char* scope, uint32_t now) {
    if (!document["bodyLedSequence"].is<uint64_t>() || document["bodyLedSequence"].as<uint64_t>() <= sequence ||
        !document["scopeId"].is<const char*>() || strcmp(document["scopeId"], scope) || !document["ready"].is<bool>()) return false;
    if (!document["ready"].as<bool>()) { clear(); sequence = document["bodyLedSequence"]; return true; }
    auto frame = document["frame"];
    if (!frame["effect"].is<const char*>() || !frame["color"]["r"].is<int>() || !frame["color"]["g"].is<int>() ||
        !frame["color"]["b"].is<int>() || !frame["brightness"].is<int>() || !frame["periodMs"].is<int>() ||
        !frame["phaseMs"].is<double>() || !frame["validForMs"].is<int>()) return false;
    const char* name = frame["effect"];
    const int nextEffect = !strcmp(name, "fixed") ? 1 : !strcmp(name, "off") ? 2 : !strcmp(name, "breathing") ? 3 : !strcmp(name, "color-cycle") ? 4 : 0;
    const int r = frame["color"]["r"], g = frame["color"]["g"], b = frame["color"]["b"], level = frame["brightness"],
      ms = frame["periodMs"], valid = frame["validForMs"];
    const double offset = frame["phaseMs"];
    if (!nextEffect || r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255 || level < 0 || level > 100 ||
        valid < 1 || valid > 3000 || !std::isfinite(offset) || offset < 0 ||
        (nextEffect <= 2 ? ms != 0 || offset != 0 : (ms != 2000 && ms != 4000 && ms != 6000 && ms != 9000 && ms != 12000) || offset >= ms)) return false;
    sequence = document["bodyLedSequence"]; base = {r, g, b}; brightness = level; period = ms; phase = offset;
    effect = nextEffect; lifetime = valid; received = now; ready = true; return true;
  }
  Color sample(uint32_t now) const {
    const uint32_t age = now - received;
    if (!ready || age >= lifetime) return {};
    Color result = base;
    if (effect == 2) return {};
    const double cycle = period ? std::fmod(phase + age, period) / period : 0;
    if (effect == 3) {
      const double scale = (1 + std::cos(cycle * 6.28318530717958647692)) / 2;
      result = {rounded(base.r * scale), rounded(base.g * scale), rounded(base.b * scale)};
    } else if (effect == 4) {
      const double h = cycle * 6; const int sector = static_cast<int>(h);
      const int v = base.r > base.g ? (base.r > base.b ? base.r : base.b) : (base.g > base.b ? base.g : base.b);
      const int rise = rounded(v * (h - sector)), fall = v - rise;
      switch (sector) {
        case 0: result = {v, rise, 0}; break; case 1: result = {fall, v, 0}; break;
        case 2: result = {0, v, rise}; break; case 3: result = {0, fall, v}; break;
        case 4: result = {rise, 0, v}; break; default: result = {v, 0, fall}; break;
      }
    }
    return {rounded(result.r * brightness / 100.0), rounded(result.g * brightness / 100.0), rounded(result.b * brightness / 100.0)};
  }
  bool fresh(uint32_t now) const { return ready && static_cast<uint32_t>(now - received) < lifetime; }
};
}
