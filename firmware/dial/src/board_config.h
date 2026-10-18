#pragma once

// GUITION JC3636K518C_I_YR1, K5-series V1/V2 schematic and pincfg.h.
// GPIO0 is BOOT / I2S, not a dial push button. Do not configure it here.
namespace Board {
constexpr int EncoderA = 8; // SSCM110100 right contact, active low (not quadrature).
constexpr int EncoderB = 7; // SSCM110100 left contact, active low.
constexpr int EncoderDirection = 1; // Reverse only after a real-device direction check.
constexpr int LcdCs = 14;
constexpr int LcdSck = 13;
constexpr int LcdD0 = 15;
constexpr int LcdD1 = 16;
constexpr int LcdD2 = 17;
constexpr int LcdD3 = 18;
constexpr int LcdReset = 21;
constexpr int LcdBacklight = 47;
constexpr int TouchSda = 11;
constexpr int TouchScl = 12;
constexpr int TouchInterrupt = 9;
constexpr int TouchReset = 10;
constexpr unsigned char TouchAddress = 0x15;
constexpr int Width = 360;
constexpr int Height = 360;
constexpr int LcdRotation = 2; // Rotate every panel write by 180 degrees.
constexpr unsigned long LcdFrequency = 40000000;
} // namespace Board
