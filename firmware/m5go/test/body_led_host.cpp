#include <cassert>
#include <fstream>
#include <iostream>
#include <string>
#include "body_led_logic.h"
static bool equal(BodyLed::Color a, BodyLed::Color b) { return a.r == b.r && a.g == b.g && a.b == b.b; }
static JsonDocument packet() {
  JsonDocument d;
  auto err = deserializeJson(d, R"({"bodyLedSequence":1,"scopeId":"all","ready":true,"frame":{"effect":"fixed","color":{"r":200,"g":100,"b":50},"brightness":50,"periodMs":0,"phaseMs":0,"validForMs":2000}})");
  assert(!err); return d;
}
int main(int argc, char** argv) {
  assert(argc == 2);
  BodyLed::Receiver receiver; auto d = packet();
  assert(receiver.accept(d.as<JsonVariantConst>(), "all", 100));
  assert(equal(receiver.sample(100), {100, 50, 25}));
  assert(equal(receiver.sample(2099), {100, 50, 25}));
  assert(equal(receiver.sample(2100), {}));
  assert(!receiver.accept(d.as<JsonVariantConst>(), "all", 2000)); // Replay does not extend lifetime.
  assert(equal(receiver.sample(2100), {}));
  for (int i = 0; i < 12; ++i) {
    receiver.reset(); d = packet(); d["bodyLedSequence"] = 2;
    switch (i) {
      case 0: d["scopeId"] = "other"; break; case 1: d["bodyLedSequence"] = -1; break;
      case 2: d["frame"]["color"]["r"] = 256; break; case 3: d["frame"]["color"]["g"] = "255"; break;
      case 4: d["frame"]["brightness"] = 101; break; case 5: d["frame"]["validForMs"] = 3001; break;
      case 6: d["frame"]["validForMs"] = 0; break; case 7: d["frame"]["effect"] = "native"; break;
      case 8: d["frame"]["periodMs"] = 6000; break; case 9: d["frame"]["phaseMs"] = 1; break;
      case 10: d["frame"]["effect"] = "breathing"; d["frame"]["periodMs"] = 3000; break;
      case 11: d["frame"]["effect"] = "breathing"; d["frame"]["periodMs"] = 6000; d["frame"]["phaseMs"] = 6000; break;
    }
    assert(!receiver.accept(d.as<JsonVariantConst>(), "all", 100)); assert(equal(receiver.sample(100), {}));
  }
  receiver.reset(); d = packet();
  assert(receiver.accept(d.as<JsonVariantConst>(), "all", UINT32_MAX - 50));
  assert(equal(receiver.sample(50), {100, 50, 25})); assert(equal(receiver.sample(2000), {}));
  d["bodyLedSequence"] = 2; d["ready"] = false;
  assert(receiver.accept(d.as<JsonVariantConst>(), "all", 100)); assert(equal(receiver.sample(100), {}));
  receiver.clear(); d = packet(); assert(!receiver.accept(d.as<JsonVariantConst>(), "all", 200));
  receiver.reset(); assert(receiver.accept(d.as<JsonVariantConst>(), "all", 200));
  std::ifstream input(argv[1]); assert(input.good()); std::string line; int vectors = 0;
  while (std::getline(input, line)) {
    JsonDocument vector; assert(!deserializeJson(vector, line));
    d = packet(); d["frame"] = vector["frame"]; receiver.reset();
    assert(receiver.accept(d.as<JsonVariantConst>(), "all", 1000));
    auto result = receiver.sample(1000 + vector["afterMs"].as<uint32_t>());
    assert(std::abs(result.r - vector["expected"][0].as<int>()) <= 1);
    assert(std::abs(result.g - vector["expected"][1].as<int>()) <= 1);
    assert(std::abs(result.b - vector["expected"][2].as<int>()) <= 1); ++vectors;
  }
  assert(vectors == 800); std::cout << "PASS parser/lifetime/replay/scope/rollover and " << vectors << " effect vectors\n";
}
