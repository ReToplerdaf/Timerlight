#!/bin/bash
# Сборка light.ino под Arduino Nano V3 (ATmega328P) без Arduino IDE.
#
# Нужны: avr-gcc, avr-libc, binutils-avr, git.
#   Ubuntu/Debian:  sudo apt install gcc-avr avr-libc binutils-avr
#   macOS (brew):   brew tap osx-cross/avr && brew install avr-gcc
#
# Ядро Arduino и FastLED скачиваются сами в light/.build при первом запуске.
# Результат: light/.build/out/light.hex
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
W="$HERE/.build"
SKETCH="$HERE/light.ino"
mkdir -p "$W"

[ -d "$W/core" ]    || git clone -q --depth 1 https://github.com/arduino/ArduinoCore-avr "$W/core"
[ -d "$W/fastled" ] || git clone -q --depth 1 https://github.com/FastLED/FastLED "$W/fastled"

OUT="$W/out"; rm -rf "$OUT"; mkdir -p "$OUT/obj"
CORE="$W/core/cores/arduino"
VAR="$W/core/variants/eightanaloginputs"     # вариант платы Nano
EEP="$W/core/libraries/EEPROM/src"
FL="$W/fastled/src"

MCU=atmega328p
DEFS="-DF_CPU=16000000L -DARDUINO=10819 -DARDUINO_AVR_NANO -DARDUINO_ARCH_AVR"
COMMON="-mmcu=$MCU -Os -ffunction-sections -fdata-sections $DEFS -I$CORE -I$VAR -I$EEP -I$FL"
CF="$COMMON -std=gnu11"
CXXF="$COMMON -std=gnu++17 -fno-exceptions -fno-threadsafe-statics -fpermissive"

compile() {
  local o="$OUT/obj/$(echo "$1" | md5sum | cut -c1-10)_$(basename "$1").o"
  case "$1" in
    *.c) avr-gcc $CF -w -c "$1" -o "$o" ;;
    *.S) avr-gcc $COMMON -x assembler-with-cpp -w -c "$1" -o "$o" ;;
    *)   avr-g++ $CXXF -w -c "$1" -o "$o" ;;
  esac
}

echo "Ядро Arduino..."
for f in $(find "$CORE" \( -name '*.c' -o -name '*.cpp' -o -name '*.S' \) | sort); do compile "$f"; done
echo "FastLED..."
for f in $(find "$FL" -name '*.cpp' -not -path '*/extras/*' | sort); do compile "$f"; done

echo "Скетч..."
printf '#include <Arduino.h>\n#line 1 "%s"\n' "$SKETCH" > "$OUT/sketch.cpp"
cat "$SKETCH" >> "$OUT/sketch.cpp"
avr-g++ $CXXF -Wall -Wextra -c "$OUT/sketch.cpp" -o "$OUT/obj/sketch.o"

avr-gcc -mmcu=$MCU -Os -Wl,--gc-sections -o "$OUT/light.elf" "$OUT/obj"/*.o -lm
avr-objcopy -O ihex -R .eeprom "$OUT/light.elf" "$OUT/light.hex"
echo
avr-size --format=avr --mcu=$MCU "$OUT/light.elf"
echo
echo "Готово: $OUT/light.hex"
echo "Прошить:  avrdude -c arduino -p atmega328p -P /dev/ttyUSB0 -b 115200 -U flash:w:$OUT/light.hex:i"
echo "          (на старом загрузчике Nano вместо 115200 нужно -b 57600)"
