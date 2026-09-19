#pragma once
#include "Arduino.h"
#include <string.h>
extern uint8_t simEeprom[1024];
struct EEPROMClass {
  template<typename T> T& get(int addr, T& t) { memcpy(&t, simEeprom+addr, sizeof(T)); return t; }
  template<typename T> const T& put(int addr, const T& t) { memcpy(simEeprom+addr, &t, sizeof(T)); return t; }
};
extern EEPROMClass EEPROM;
