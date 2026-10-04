#include <Arduino.h>
#include <esp32-hal-rmt.h>
#include "body_led.h"

namespace BodyLed {
namespace { rmt_obj_t* output = nullptr; Color previous{-1, -1, -1}; }
bool begin() {
  // Original M5GO Bottom: ten SK6812 RGB pixels on G15. No other GPIO is used.
  output = rmtInit(15, RMT_TX_MODE, RMT_MEM_64);
  if (!output) return false;
  rmtSetTick(output, 100); pinMode(15, OUTPUT_OPEN_DRAIN);
  write({}); return true;
}
void write(Color color) {
  if (!output || (color.r == previous.r && color.g == previous.g && color.b == previous.b)) return;
  rmt_data_t data[241]{};
  int index = 0;
  for (int pixel = 0; pixel < 10; ++pixel)
    for (int channel : {color.g, color.r, color.b})
      for (int bit = 7; bit >= 0; --bit) {
        const bool one = channel & (1 << bit);
        data[index].level0 = 1; data[index].duration0 = one ? 6 : 3;
        data[index].level1 = 0; data[index].duration1 = one ? 6 : 9; ++index;
      }
  data[index].level0 = data[index].level1 = 0;
  data[index].duration0 = data[index].duration1 = 500; // 100 us reset/latch.
  if (rmtWriteBlocking(output, data, 241)) previous = color;
}
}
