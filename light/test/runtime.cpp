#include "Arduino.h"
#include "FastLED.h"
#include "EEPROM.h"
unsigned long simMillis = 0;
unsigned long simAutoTick = 0;
int simPin[24];
void (*simPinHook)(unsigned long) = 0;
int simShowCount = 0;
uint8_t simEeprom[1024];
EEPROMClass EEPROM;
CFastLED FastLED;
CRGB* simLeds = 0;
int simLedCount = 0;
ShowEvt simShowLog[4096];
int simShowLogN = 0;
void CFastLED::show() { simShowCount++;
  if (simLeds && simShowLogN < 4096) { ShowEvt e; e.t = simMillis; e.r = simLeds[0].r; e.g = simLeds[0].g; e.b = simLeds[0].b;
    if (simShowLogN==0 || simShowLog[simShowLogN-1].r!=e.r || simShowLog[simShowLogN-1].g!=e.g || simShowLog[simShowLogN-1].b!=e.b) simShowLog[simShowLogN++] = e; } }
unsigned long millis() { simMillis += simAutoTick; if (simPinHook) simPinHook(simMillis); return simMillis; }
void delay(unsigned long ms) { unsigned long t = simMillis + ms; while (simMillis < t) { simMillis += (simAutoTick?simAutoTick:1); if (simPinHook) simPinHook(simMillis); } }
void pinMode(uint8_t, uint8_t) {}
int digitalRead(uint8_t pin) { if (simPinHook) simPinHook(simMillis); return simPin[pin]; }
