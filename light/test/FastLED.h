#pragma once
#include "Arduino.h"
enum EChipset { WS2815, WS2812B };
enum EOrder   { RGB, GRB, BRG };
struct CRGB {
  uint8_t r, g, b;
  CRGB() : r(0), g(0), b(0) {}
  CRGB(uint8_t rr, uint8_t gg, uint8_t bb) : r(rr), g(gg), b(bb) {}
  enum HTMLColorCode { Black=0x000000, Red=0xFF0000, Green=0x008000, Blue=0x0000FF,
                       White=0xFFFFFF, Yellow=0xFFFF00 };
  CRGB(uint32_t c) : r((c>>16)&0xFF), g((c>>8)&0xFF), b(c&0xFF) {}
  CRGB(HTMLColorCode c) : r((((uint32_t)c)>>16)&0xFF), g((((uint32_t)c)>>8)&0xFF), b(((uint32_t)c)&0xFF) {}
  CRGB& nscale8_video(uint8_t scale) {
    uint8_t nz = (r||g||b) ? 1 : 0;
    r = r ? (((uint16_t)r*scale)>>8)+nz : 0;
    g = g ? (((uint16_t)g*scale)>>8)+nz : 0;
    b = b ? (((uint16_t)b*scale)>>8)+nz : 0;
    return *this;
  }
  bool operator==(const CRGB&o) const { return r==o.r&&g==o.g&&b==o.b; }
  bool operator!=(const CRGB&o) const { return !(*this==o); }
  bool isBlack() const { return !r && !g && !b; }
};
inline void fill_solid(CRGB* p, int n, CRGB c) { for (int i=0;i<n;i++) p[i]=c; }
extern int simShowCount;
extern CRGB* simLeds;
extern int simLedCount;
struct ShowEvt { unsigned long t; uint8_t r,g,b; };
extern ShowEvt simShowLog[4096];
extern int simShowLogN;
struct CFastLED {
  template<EChipset C, uint8_t PIN, EOrder O> void addLeds(CRGB* p, int n) { simLeds = p; simLedCount = n; }
  void setBrightness(uint8_t) {}
  void show();
};
extern CFastLED FastLED;
