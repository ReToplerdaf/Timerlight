#pragma once
#include <stdint.h>
#include <stddef.h>
#define LOW  0
#define HIGH 1
#define INPUT_PULLUP 2
extern unsigned long simMillis;
extern unsigned long simAutoTick;
extern int  simPin[24];
extern void (*simPinHook)(unsigned long);
unsigned long millis();
void delay(unsigned long ms);
void pinMode(uint8_t, uint8_t);
int  digitalRead(uint8_t pin);
