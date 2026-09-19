/*
  =====================================================================
  Скетч 2.0 — стоп-сигналы / поворотники / габариты на ленте WS2815
  =====================================================================
  Плата:      Arduino Nano V3
  Лента:      WS2815, 144 светодиода, DATA -> D11
  Библиотека: FastLED (3.4 и новее)

  Входы (все INPUT_PULLUP, активный уровень — LOW):
    D2 — кнопка калибровки (удержание 4 с)
    D3 — левый поворот / вход сигнала при калибровке
    D4 — габариты
    D5 — правый поворот
    D6 — багажник
    D7 — стоп
    D8 — задний ход

  ---------------------------------------------------------------------
  НУМЕРАЦИЯ. В ТЗ диоды пронумерованы 1..144, в коде массив 0..143.
  Везде ниже в комментариях указан номер из ТЗ, в коде — индекс на 1
  меньше. Например «заполнение 73-144» — это индексы 72..143.

  ---------------------------------------------------------------------
  РАСХОЖДЕНИЯ С ТЗ (в самом ТЗ эти места противоречивы) — проверьте:

  1. Багажник (D6). В разделе «Входы» сказано «управление — низкий
     уровень», а в Условии 5.1 — «Если Pin 6 = HIGH». Принят LOW, как
     у остальных входов: при INPUT_PULLUP неподключённый вход всегда
     читается как HIGH, то есть вариант «HIGH» означал бы непрерывное
     мигание жёлтым при закрытом багажнике. Чтобы вернуть букву
     Условия 5.1, поставьте TRUNK_ACTIVE_LEVEL HIGH.

  2. Условие 7 (гашение стопа). В ТЗ «гасить 144-73, 0-36»: диоды
     37..72 не гасятся вообще, и два прохода разной длины (72 и 36)
     закончились бы в разное время. Реализовано «144-73 и 1-72» —
     два прохода от краёв к середине, вся лента гаснет полностью.
     Если нужен другой рисунок — см. PAT_ENDS.

  3. Приоритет, когда сигналов несколько сразу (в ТЗ задан только для
     стоп+задний ход, Условие 10):
       стоп > задний ход+поворот > задний ход > аварийка >
       поворот > багажник > габариты > выключено.
     Тормоз всегда важнее всего.

  4. Условие 13 измеряет и длительность импульса (начало->конец), и
     период между импульсами. В EEPROM пишутся оба, но время
     заполнения считается от ДЛИТЕЛЬНОСТИ импульса: заполнение должно
     успевать пройти, пока штатный поворотник горит, иначе эффект
     обрывается на середине. Доля от импульса — turnFillPercent.
  =====================================================================
*/

#include <FastLED.h>
#include <EEPROM.h>

// ---------------------------------------------------------------------
// Аппаратные настройки
// ---------------------------------------------------------------------
#define LED_PIN       11
#define NUM_LEDS      144
#define LED_TYPE      WS2815
#define COLOR_ORDER   GRB          // WS2815 — GRB; если цвета путаются, смените

#define PIN_CALIBRATE 2
#define PIN_LEFT      3
#define PIN_GABARIT   4
#define PIN_RIGHT     5
#define PIN_TRUNK     6
#define PIN_BRAKE     7
#define PIN_REVERSE   8

#define TRUNK_ACTIVE_LEVEL LOW     // см. расхождение №1 в шапке файла

CRGB leds[NUM_LEDS];
bool dirty = false;                // буфер изменился — нужен FastLED.show()

// ---------------------------------------------------------------------
// Настраиваемые параметры эффектов — всё, что ТЗ просит «с возможностью
// менять»: цвет, яркость, скорость, количество диодов за шаг.
// Время задаётся не «мс на шаг», а «мс на весь проход» — так нагляднее
// и не зависит от количества диодов за шаг.
// ---------------------------------------------------------------------

// Условия 1, 2, 3, 11, 12 — поворотники (пересчитываются калибровкой)
CRGB     turnColor        = CRGB(255, 90, 0);   // оранжевый
uint16_t turnFillMs       = 350;                // время одного заполнения
uint8_t  turnStepLeds     = 3;                  // диодов за один шаг
uint8_t  turnStepLedsBase = 3;                  // исходное значение для калибровки
uint8_t  turnFillPercent  = 90;                 // доля импульса под заполнение

// Условия 4, 5 — габариты
CRGB     gabaritColor      = CRGB::Red;
uint8_t  gabaritBrightness = 102;               // 40% от 255
uint16_t gabaritFillMs     = 300;               // усл. 4 — заполнение 73-144, 72-1
uint16_t gabaritOffMs      = 400;               // усл. 5 — гашение тем же рисунком
uint8_t  gabaritStepLeds   = 2;

// Условие 5.1 — багажник
CRGB     trunkColor      = CRGB::Yellow;
uint16_t trunkBlinkOnMs  = 200;
uint16_t trunkBlinkOffMs = 200;
uint8_t  trunkBlinkTimes = 3;                   // вспышек на каждую группу

// Условия 6, 7, 10 — стоп
CRGB     brakeColor    = CRGB::Red;             // яркость 100%
uint16_t brakeFillMs   = 120;                   // усл. 6 — четырьмя проходами
uint16_t brakeOffMs    = 250;                   // усл. 7 — от краёв к середине
uint8_t  brakeStepLeds = 2;

// Условия 8, 9 — задний ход
CRGB     reverseColor    = CRGB::White;         // яркость 100%
uint16_t reverseFillMs   = 250;                 // усл. 8 — из середины к краям
uint16_t reverseOffMs    = 300;                 // усл. 9 — тем же рисунком
uint8_t  reverseStepLeds = 2;

// Антидребезг входов. В ТЗ его нет, но контакты реле и концевиков дребезжат,
// а помеха на 12 В линии иначе даёт мгновенную вспышку ленты. Вход должен
// продержаться в новом состоянии столько миллисекунд, чтобы быть принятым.
// Поставьте 0, чтобы читать входы «как есть», без задержки.
const uint8_t INPUT_DEBOUNCE_MS = 15;

// Условие 13 — калибровка
const unsigned long CALIBRATE_HOLD_MS   = 4000; // удержание D2 для входа
const uint8_t  CALIB_PULSES             = 4;    // сколько импульсов измеряем
const unsigned long CALIB_WAIT_MS       = 30000;// ждём первый импульс, потом отказ
const unsigned long CALIB_PULSE_TIMEOUT = 5000; // таймаут внутри серии
const unsigned long CALIB_DEBOUNCE_MS   = 5;    // антидребезг входа
const uint16_t CALIB_GREEN_ON_MS        = 300;
const uint16_t CALIB_GREEN_OFF_MS       = 300;
const uint16_t TURN_MIN_FILL_MS         = 60;   // ниже этого заполнение не сжимаем
const uint16_t TURN_MIN_STEP_MS         = 6;    // кадр на 144 диода идёт ~4.3 мс

// ---------------------------------------------------------------------
// Условие 5.1 — группы диодов багажника.
// В ТЗ: группа 1 (136-124, 114-103, 93-81), группа 2 (64-52, 42-31, 21-9).
// Здесь те же диапазоны в индексах массива (на 1 меньше).
// ---------------------------------------------------------------------
struct Range { uint8_t first; uint8_t last; };

const uint8_t TRUNK_RANGES = 3;
const Range trunkGroup1[TRUNK_RANGES] = { {123, 135}, {102, 113}, {80, 92} };
const Range trunkGroup2[TRUNK_RANGES] = { { 51,  63}, { 30,  41}, { 8, 20} };

// ---------------------------------------------------------------------
// Рисунки заполнения/гашения.
// Run — один «бегунок»: с какого индекса и в какую сторону идёт.
// Pattern — набор бегунков, которые идут одновременно, шаг в шаг.
// ---------------------------------------------------------------------
struct Run     { int16_t start; int8_t dir; };
struct Pattern { const Run *runs; uint8_t count; uint8_t len; };

// усл. 1 — заполнение 1-144
const Run RUN_TURN_L[]  = { {  0, +1} };
// усл. 2 — заполнение 144-1
const Run RUN_TURN_R[]  = { {143, -1} };
// усл. 3, 4, 5, 8, 9 — из середины к краям: 73-144 и 72-1
const Run RUN_CENTER[]  = { { 72, +1}, { 71, -1} };
// усл. 7 — от краёв к середине: 144-73 и 1-72 (см. расхождение №2)
const Run RUN_ENDS[]    = { {143, -1}, {  0, +1} };
// усл. 6 — четыре четверти сразу: 144-109, 73-108, 72-37, 1-36
const Run RUN_BRAKE[]   = { {143, -1}, { 72, +1}, { 71, -1}, {  0, +1} };
// усл. 11 — заполнение 73-144
const Run RUN_COMBO_L[] = { { 72, +1} };
// усл. 12 — заполнение 72-1
const Run RUN_COMBO_R[] = { { 71, -1} };

const Pattern PAT_TURN_L  = { RUN_TURN_L,  1, NUM_LEDS     };
const Pattern PAT_TURN_R  = { RUN_TURN_R,  1, NUM_LEDS     };
const Pattern PAT_CENTER  = { RUN_CENTER,  2, NUM_LEDS / 2 };
const Pattern PAT_ENDS    = { RUN_ENDS,    2, NUM_LEDS / 2 };
const Pattern PAT_BRAKE   = { RUN_BRAKE,   4, NUM_LEDS / 4 };
const Pattern PAT_COMBO_L = { RUN_COMBO_L, 1, NUM_LEDS / 2 };
const Pattern PAT_COMBO_R = { RUN_COMBO_R, 1, NUM_LEDS / 2 };

// ---------------------------------------------------------------------
// Режимы
// ---------------------------------------------------------------------
enum Mode {
  MODE_OFF,
  MODE_LEFT,
  MODE_RIGHT,
  MODE_HAZARD,
  MODE_GABARIT,
  MODE_TRUNK,
  MODE_BRAKE,
  MODE_REVERSE,
  MODE_REVERSE_LEFT,
  MODE_REVERSE_RIGHT
};

Mode currentMode        = MODE_OFF;
Mode previousActiveMode = MODE_OFF;   // из какого режима гасим (усл. 5/7/9)

// ---------------------------------------------------------------------
// Состояние текущего прохода
// ---------------------------------------------------------------------
struct Sweep {
  const Pattern *pat;
  CRGB     color;
  uint16_t stepDelay;
  uint8_t  ledsPerStep;
  uint8_t  pos;
  bool     done;
  unsigned long lastStep;
};
Sweep sweep = { NULL, CRGB::Black, 0, 1, 0, true, 0 };

// Состояние мигания багажника
uint8_t  trunkGroupIdx   = 0;
uint8_t  trunkBlinkCount = 0;
bool     trunkOn         = false;
unsigned long trunkLastToggle = 0;

// ---------------------------------------------------------------------
// Калибровка (условие 13)
// ---------------------------------------------------------------------
const int EEPROM_ADDR = 0;
const uint16_t CALIB_MAGIC = 0xC0DE;

struct CalibRecord {
  uint16_t magic;
  uint16_t onMs;       // средняя длительность импульса поворотника
  uint16_t periodMs;   // средний интервал между началами импульсов
  uint16_t check;
};
CalibRecord calib = { CALIB_MAGIC, 0, 0, 0 };

// ---------------------------------------------------------------------
// Прототипы
// ---------------------------------------------------------------------
uint8_t readInputs();
Mode readMode();
void applyMode(Mode m);
void runMode();
void sweepBegin(const Pattern &p, CRGB color, uint16_t durationMs, uint8_t ledsPerStep);
bool sweepTick();
void patternBlank(const Pattern &p);
void clearStrip();
void fillRange(uint8_t first, uint8_t last, CRGB color);
CRGB gabaritShade();
void runTurnRepeat();
void runTrunkBlink();
void startOffWipe();
bool calibrationRequested();
void runCalibrationMode();
void applyCalibration();
void loadCalibration();
void saveCalibration();

// =====================================================================
void setup() {
  pinMode(PIN_CALIBRATE, INPUT_PULLUP);
  pinMode(PIN_LEFT,      INPUT_PULLUP);
  pinMode(PIN_GABARIT,   INPUT_PULLUP);
  pinMode(PIN_RIGHT,     INPUT_PULLUP);
  pinMode(PIN_TRUNK,     INPUT_PULLUP);
  pinMode(PIN_BRAKE,     INPUT_PULLUP);
  pinMode(PIN_REVERSE,   INPUT_PULLUP);

  FastLED.addLeds<LED_TYPE, LED_PIN, COLOR_ORDER>(leds, NUM_LEDS);
  FastLED.setBrightness(255);
  clearStrip();
  FastLED.show();
  dirty = false;

  loadCalibration();
}

void loop() {
  if (calibrationRequested()) {
    runCalibrationMode();
    return;
  }

  Mode m = readMode();
  if (m != currentMode) {
    if (currentMode != MODE_OFF) previousActiveMode = currentMode;
    currentMode = m;
    applyMode(m);
  }

  runMode();

  if (dirty) {
    FastLED.show();
    dirty = false;
  }
}

// ---------------------------------------------------------------------
// Чтение входов с антидребезгом: новое состояние принимается, только
// когда оно продержалось INPUT_DEBOUNCE_MS подряд.
// ---------------------------------------------------------------------
#define IN_LEFT    0x01
#define IN_RIGHT   0x02
#define IN_GABARIT 0x04
#define IN_TRUNK   0x08
#define IN_BRAKE   0x10
#define IN_REVERSE 0x20

uint8_t readInputs() {
  static uint8_t stable = 0, lastRaw = 0;
  static unsigned long changedAt = 0;

  uint8_t raw = 0;
  if (digitalRead(PIN_LEFT)    == LOW)                raw |= IN_LEFT;
  if (digitalRead(PIN_RIGHT)   == LOW)                raw |= IN_RIGHT;
  if (digitalRead(PIN_GABARIT) == LOW)                raw |= IN_GABARIT;
  if (digitalRead(PIN_TRUNK)   == TRUNK_ACTIVE_LEVEL) raw |= IN_TRUNK;
  if (digitalRead(PIN_BRAKE)   == LOW)                raw |= IN_BRAKE;
  if (digitalRead(PIN_REVERSE) == LOW)                raw |= IN_REVERSE;

  if (raw != lastRaw) {
    lastRaw   = raw;
    changedAt = millis();
  } else if (millis() - changedAt >= INPUT_DEBOUNCE_MS) {
    stable = raw;
  }
  return stable;
}

// ---------------------------------------------------------------------
// Определение режима по входам (приоритет — см. расхождение №3 в шапке)
// ---------------------------------------------------------------------
Mode readMode() {
  uint8_t in = readInputs();
  bool left    = in & IN_LEFT;
  bool right   = in & IN_RIGHT;
  bool gabarit = in & IN_GABARIT;
  bool trunk   = in & IN_TRUNK;
  bool brake   = in & IN_BRAKE;
  bool reverse = in & IN_REVERSE;

  if (brake)                 return MODE_BRAKE;          // усл. 6 и 10
  if (reverse && left)       return MODE_REVERSE_LEFT;   // усл. 11
  if (reverse && right)      return MODE_REVERSE_RIGHT;  // усл. 12
  if (reverse)               return MODE_REVERSE;        // усл. 8
  if (left && right)         return MODE_HAZARD;         // усл. 3
  if (left)                  return MODE_LEFT;           // усл. 1
  if (right)                 return MODE_RIGHT;          // усл. 2
  if (trunk)                 return MODE_TRUNK;          // усл. 5.1
  if (gabarit)               return MODE_GABARIT;        // усл. 4
  return MODE_OFF;                                       // усл. 5 / 7 / 9
}

// ---------------------------------------------------------------------
// Вход в режим: любое заполнение начинается с погашенной ленты, иначе
// на ней остались бы диоды от предыдущего эффекта.
// ---------------------------------------------------------------------
void applyMode(Mode m) {
  switch (m) {
    case MODE_LEFT:                                        // усл. 1
      clearStrip();
      sweepBegin(PAT_TURN_L, turnColor, turnFillMs, turnStepLeds);
      break;

    case MODE_RIGHT:                                       // усл. 2
      clearStrip();
      sweepBegin(PAT_TURN_R, turnColor, turnFillMs, turnStepLeds);
      break;

    case MODE_HAZARD:                                      // усл. 3
      clearStrip();
      sweepBegin(PAT_CENTER, turnColor, turnFillMs, turnStepLeds);
      break;

    case MODE_GABARIT:                                     // усл. 4
      clearStrip();
      sweepBegin(PAT_CENTER, gabaritShade(), gabaritFillMs, gabaritStepLeds);
      break;

    case MODE_TRUNK:                                       // усл. 5.1
      clearStrip();
      sweep.pat  = NULL;
      sweep.done = true;
      trunkGroupIdx   = 0;
      trunkBlinkCount = 0;
      trunkOn         = false;
      trunkLastToggle = millis() - trunkBlinkOffMs;        // мигнуть сразу
      break;

    case MODE_BRAKE:                                       // усл. 6 и 10
      clearStrip();
      sweepBegin(PAT_BRAKE, brakeColor, brakeFillMs, brakeStepLeds);
      break;

    case MODE_REVERSE:                                     // усл. 8
      clearStrip();
      sweepBegin(PAT_CENTER, reverseColor, reverseFillMs, reverseStepLeds);
      break;

    case MODE_REVERSE_LEFT:                                // усл. 11
      clearStrip();
      fillRange(0, 71, reverseColor);                      // 1-72 белым мгновенно
      sweepBegin(PAT_COMBO_L, turnColor, turnFillMs, turnStepLeds);
      break;

    case MODE_REVERSE_RIGHT:                               // усл. 12
      clearStrip();
      fillRange(72, 143, reverseColor);                    // 73-144 белым мгновенно
      sweepBegin(PAT_COMBO_R, turnColor, turnFillMs, turnStepLeds);
      break;

    case MODE_OFF:                                         // усл. 5 / 7 / 9
      startOffWipe();
      break;
  }
}

// ---------------------------------------------------------------------
// Работа режима — вызывается каждый проход loop()
// ---------------------------------------------------------------------
void runMode() {
  switch (currentMode) {
    // Заполнить -> мгновенно погасить -> повторить
    case MODE_LEFT:
    case MODE_RIGHT:
    case MODE_HAZARD:
    case MODE_REVERSE_LEFT:
    case MODE_REVERSE_RIGHT:
      runTurnRepeat();
      break;

    // Заполнить и держать
    case MODE_GABARIT:
    case MODE_BRAKE:
    case MODE_REVERSE:
      sweepTick();
      break;

    case MODE_TRUNK:
      runTrunkBlink();
      break;

    case MODE_OFF:
      sweepTick();
      break;
  }
}

// Условия 1/2/3/11/12: заполнили — держим один шаг, гасим мгновенно,
// начинаем заново. В комбо-режимах гаснет только «бегущая» половина,
// белая половина продолжает гореть.
void runTurnRepeat() {
  sweepTick();
  if (sweep.done && sweep.pat != NULL &&
      millis() - sweep.lastStep >= sweep.stepDelay) {
    patternBlank(*sweep.pat);
    sweep.pos      = 0;
    sweep.done     = false;
    sweep.lastStep = millis();
    dirty = true;
  }
}

// Условия 5/7/9: гашение своим рисунком, у каждого источника свой.
void startOffWipe() {
  switch (previousActiveMode) {
    case MODE_GABARIT:                                     // усл. 5
      sweepBegin(PAT_CENTER, CRGB::Black, gabaritOffMs, gabaritStepLeds);
      break;

    case MODE_BRAKE:                                       // усл. 7
      sweepBegin(PAT_ENDS, CRGB::Black, brakeOffMs, brakeStepLeds);
      break;

    case MODE_REVERSE:                                     // усл. 9
    case MODE_REVERSE_LEFT:
    case MODE_REVERSE_RIGHT:
      sweepBegin(PAT_CENTER, CRGB::Black, reverseOffMs, reverseStepLeds);
      break;

    default:                                               // усл. 1/2/3/5.1
      clearStrip();                                        // гаснет мгновенно
      sweep.pat  = NULL;
      sweep.done = true;
      break;
  }
}

// ---------------------------------------------------------------------
// Условие 5.1 — мигание двумя группами по очереди
// ---------------------------------------------------------------------
void runTrunkBlink() {
  unsigned long now = millis();
  uint16_t interval = trunkOn ? trunkBlinkOnMs : trunkBlinkOffMs;
  if (now - trunkLastToggle < interval) return;
  trunkLastToggle = now;
  trunkOn = !trunkOn;

  clearStrip();                       // остальная лента не участвует
  if (trunkOn) {
    const Range *group = (trunkGroupIdx == 0) ? trunkGroup1 : trunkGroup2;
    for (uint8_t i = 0; i < TRUNK_RANGES; i++) {
      fillRange(group[i].first, group[i].last, trunkColor);
    }
  } else {
    // вспышка «включилась и погасла» — считаем её
    trunkBlinkCount++;
    if (trunkBlinkCount >= trunkBlinkTimes) {
      trunkBlinkCount = 0;
      trunkGroupIdx = 1 - trunkGroupIdx;
    }
  }
  dirty = true;
}

// ---------------------------------------------------------------------
// Движок проходов
// ---------------------------------------------------------------------
void sweepBegin(const Pattern &p, CRGB color, uint16_t durationMs, uint8_t ledsPerStep) {
  if (ledsPerStep < 1) ledsPerStep = 1;
  uint16_t steps = (p.len + ledsPerStep - 1) / ledsPerStep;
  if (steps == 0) steps = 1;

  sweep.pat         = &p;
  sweep.color       = color;
  sweep.ledsPerStep = ledsPerStep;
  sweep.stepDelay   = durationMs / steps;
  sweep.pos         = 0;
  sweep.done        = false;
  sweep.lastStep    = millis();
}

bool sweepTick() {
  if (sweep.pat == NULL || sweep.done) return false;

  unsigned long now = millis();
  if (now - sweep.lastStep < sweep.stepDelay) return false;
  sweep.lastStep = now;

  for (uint8_t n = 0; n < sweep.ledsPerStep && sweep.pos < sweep.pat->len; n++) {
    for (uint8_t r = 0; r < sweep.pat->count; r++) {
      int16_t idx = sweep.pat->runs[r].start + (int16_t)sweep.pat->runs[r].dir * (int16_t)sweep.pos;
      if (idx >= 0 && idx < NUM_LEDS) leds[idx] = sweep.color;
    }
    sweep.pos++;
  }
  if (sweep.pos >= sweep.pat->len) sweep.done = true;

  dirty = true;
  return true;
}

// Погасить ровно те диоды, которые закрашивает этот рисунок
void patternBlank(const Pattern &p) {
  for (uint8_t i = 0; i < p.len; i++) {
    for (uint8_t r = 0; r < p.count; r++) {
      int16_t idx = p.runs[r].start + (int16_t)p.runs[r].dir * (int16_t)i;
      if (idx >= 0 && idx < NUM_LEDS) leds[idx] = CRGB::Black;
    }
  }
}

void clearStrip() {
  fill_solid(leds, NUM_LEDS, CRGB::Black);
  dirty = true;
}

void fillRange(uint8_t first, uint8_t last, CRGB color) {
  for (uint16_t i = first; i <= last && i < NUM_LEDS; i++) leds[i] = color;
  dirty = true;
}

// Условие 4 — красный, приглушённый до gabaritBrightness
CRGB gabaritShade() {
  CRGB c = gabaritColor;
  c.nscale8_video(gabaritBrightness);
  return c;
}

// =====================================================================
// Условие 13 — калибровка
// =====================================================================

// Вход в режим: D2 удержан 4 секунды. Пока кнопка держится, обычные
// эффекты продолжают работать — лента не замирает.
bool calibrationRequested() {
  static unsigned long pressStart = 0;

  if (digitalRead(PIN_CALIBRATE) == HIGH) {
    pressStart = 0;
    return false;
  }
  if (pressStart == 0) {
    pressStart = millis();
    return false;
  }
  if (millis() - pressStart >= CALIBRATE_HOLD_MS) {
    pressStart = 0;
    return true;
  }
  return false;
}

inline bool pinActive(uint8_t pin) { return digitalRead(pin) == LOW; }

// Калибровка блокирует ленту, поэтому нажатие на тормоз её прерывает:
// стоп-сигнал важнее настройки.
inline bool calibAborted() { return pinActive(PIN_BRAKE); }

void blinkOnce(CRGB color, uint16_t onMs, uint16_t offMs) {
  fill_solid(leds, NUM_LEDS, color);
  FastLED.show();
  delay(onMs);
  fill_solid(leds, NUM_LEDS, CRGB::Black);
  FastLED.show();
  delay(offMs);
}

// Ждёт устойчивого уровня на пине. edgeAt — момент перехода.
bool waitLevel(uint8_t pin, bool active, unsigned long timeoutMs, unsigned long &edgeAt) {
  unsigned long t0 = millis();
  while (millis() - t0 < timeoutMs) {
    if (calibAborted()) return false;
    if (pinActive(pin) == active) {
      unsigned long edge = millis();
      bool stable = true;
      while (millis() - edge < CALIB_DEBOUNCE_MS) {
        if (pinActive(pin) != active) { stable = false; break; }
      }
      if (stable) { edgeAt = edge; return true; }
    }
  }
  return false;
}

// Мигает зелёным, пока не придёт первый импульс на D3.
bool blinkUntilSignal(unsigned long &edgeAt) {
  unsigned long t0 = millis();
  unsigned long lastToggle = 0;
  bool on = false;

  while (millis() - t0 < CALIB_WAIT_MS) {
    if (calibAborted()) return false;

    if (pinActive(PIN_LEFT)) {
      unsigned long edge = millis();
      bool stable = true;
      while (millis() - edge < CALIB_DEBOUNCE_MS) {
        if (!pinActive(PIN_LEFT)) { stable = false; break; }
      }
      if (stable) { edgeAt = edge; return true; }
    }

    if (millis() - lastToggle >= (unsigned long)(on ? CALIB_GREEN_ON_MS : CALIB_GREEN_OFF_MS)) {
      lastToggle = millis();
      on = !on;
      fill_solid(leds, NUM_LEDS, on ? CRGB(CRGB::Green) : CRGB(CRGB::Black));
      FastLED.show();
    }
  }
  return false;
}

// Синяя вспышка длиной с сам импульс. Одновременно следит за началом
// следующего импульса: при 50% скважности он приходит как раз под конец
// вспышки, и без этого его можно было бы пропустить.
void flashWatching(CRGB color, unsigned long durMs, uint8_t pin, unsigned long &nextEdge) {
  fill_solid(leds, NUM_LEDS, color);
  FastLED.show();

  unsigned long t0 = millis();
  bool prevActive = pinActive(pin);
  while (millis() - t0 < durMs) {
    bool active = pinActive(pin);
    if (active && !prevActive && nextEdge == 0) nextEdge = millis();
    prevActive = active;
  }

  fill_solid(leds, NUM_LEDS, CRGB::Black);
  FastLED.show();
}

void calibrationFailed() {
  for (uint8_t i = 0; i < 3; i++) blinkOnce(CRGB::Red, 150, 150);
  clearStrip();
  FastLED.show();
  dirty = false;
  currentMode = MODE_OFF;
  previousActiveMode = MODE_OFF;
  sweep.pat  = NULL;
  sweep.done = true;
}

void runCalibrationMode() {
  currentMode = MODE_OFF;
  previousActiveMode = MODE_OFF;
  sweep.pat  = NULL;
  sweep.done = true;

  // 1. Мигать зелёным до первого сигнала на D3
  unsigned long pulseStart = 0;
  if (!blinkUntilSignal(pulseStart)) { calibrationFailed(); return; }

  // 2. Измерить CALIB_PULSES импульсов: длительность и период
  uint32_t onSum = 0, periodSum = 0;
  uint8_t  periodCount = 0;
  unsigned long prevStart = 0;

  for (uint8_t i = 0; i < CALIB_PULSES; i++) {
    unsigned long pulseEnd;
    if (!waitLevel(PIN_LEFT, false, CALIB_PULSE_TIMEOUT, pulseEnd)) { calibrationFailed(); return; }

    unsigned long onMs = pulseEnd - pulseStart;
    onSum += onMs;
    if (i > 0) { periodSum += pulseStart - prevStart; periodCount++; }
    prevStart = pulseStart;

    // 3. Мигнуть синим на длину импульса
    unsigned long caught = 0;
    flashWatching(CRGB::Blue, onMs, PIN_LEFT, caught);

    if (i + 1 < CALIB_PULSES) {
      if (caught) {
        pulseStart = caught;                 // начало поймали во время вспышки
      } else if (!waitLevel(PIN_LEFT, true, CALIB_PULSE_TIMEOUT, pulseStart)) {
        calibrationFailed();
        return;
      }
    }
  }

  // 4. Сохранить средние значения
  uint32_t avgOn     = onSum / CALIB_PULSES;
  uint32_t avgPeriod = periodCount ? (periodSum / periodCount) : 0;
  if (avgOn     > 60000) avgOn     = 60000;
  if (avgPeriod > 60000) avgPeriod = 60000;
  if (avgOn == 0) { calibrationFailed(); return; }

  calib.onMs     = (uint16_t)avgOn;
  calib.periodMs = (uint16_t)avgPeriod;
  saveCalibration();
  applyCalibration();

  // 5. Пять белых вспышек за 2 секунды и выход
  for (uint8_t i = 0; i < 5; i++) blinkOnce(CRGB::White, 200, 200);

  clearStrip();
  FastLED.show();
  dirty = false;
  currentMode = MODE_OFF;
  previousActiveMode = MODE_OFF;
  sweep.pat  = NULL;
  sweep.done = true;
}

// Пересчитывает время заполнения и количество диодов за шаг для всех
// условий, где участвуют D3 и D5 (усл. 1, 2, 3, 11, 12).
void applyCalibration() {
  if (calib.onMs == 0) return;

  uint32_t fill = (uint32_t)calib.onMs * turnFillPercent / 100;
  if (fill < TURN_MIN_FILL_MS) fill = TURN_MIN_FILL_MS;
  if (fill > 60000)            fill = 60000;
  turnFillMs = (uint16_t)fill;

  // Шаг не должен быть короче времени передачи кадра на ленту, иначе
  // заполнение всё равно не успеет и растянется. Если импульс короткий —
  // берём больше диодов за шаг.
  uint8_t perStep = turnStepLedsBase;
  if (perStep < 1) perStep = 1;
  while (perStep < NUM_LEDS) {
    uint16_t steps = (NUM_LEDS + perStep - 1) / perStep;
    if (turnFillMs / steps >= TURN_MIN_STEP_MS) break;
    perStep++;
  }
  turnStepLeds = perStep;
}

uint16_t calibChecksum(const CalibRecord &r) {
  return (uint16_t)(r.magic + (uint16_t)(r.onMs * 3) + (uint16_t)(r.periodMs * 7));
}

void loadCalibration() {
  CalibRecord r;
  EEPROM.get(EEPROM_ADDR, r);
  if (r.magic == CALIB_MAGIC && r.check == calibChecksum(r) &&
      r.onMs > 0 && r.onMs < 10000) {
    calib = r;
    applyCalibration();
  }
}

void saveCalibration() {
  calib.magic = CALIB_MAGIC;
  calib.check = calibChecksum(calib);
  EEPROM.put(EEPROM_ADDR, calib);
}
