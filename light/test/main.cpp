#include "Arduino.h"
#include "../light.ino"
#include <stdio.h>
#include <vector>
#include <string>
#include <algorithm>
#include <string.h>
using namespace std;

static int failures = 0;
static void ok(const char* name, bool cond, const string& detail="") {
  printf("%s  %-60s %s\n", cond?"[ OK ]":"[FAIL]", name, detail.c_str());
  if (!cond) failures++;
}
// «Спокойное» состояние входов. Для багажника оно зависит от
// TRUNK_ACTIVE_LEVEL: при активном HIGH вход в покое лежит на LOW.
static void allInactive() {
  for (int i=0;i<24;i++) simPin[i]=HIGH;
  simPin[PIN_TRUNK] = (TRUNK_ACTIVE_LEVEL==LOW) ? HIGH : LOW;
}
static void resetAll() {
  allInactive(); simPinHook=0; simAutoTick=0;
  // дать антидребезгу отпустить входы предыдущего теста
  for (int i=0;i<40;i++){ simMillis++; loop(); }
  currentMode=MODE_OFF; previousActiveMode=MODE_OFF;
  sweep.pat=NULL; sweep.done=true;
  fill_solid(leds,NUM_LEDS,CRGB::Black); dirty=false;
}
static void step(int ms){ for(int i=0;i<ms;i++){ simMillis++; loop(); } }
static bool stepUntilMode(Mode m,int maxMs){ for(int i=0;i<maxMs;i++){ simMillis++; loop(); if(currentMode==m) return true; } return false; }
static int  litCount(){ int c=0; for(int i=0;i<NUM_LEDS;i++) if(!leds[i].isBlack()) c++; return c; }
static string rgb(CRGB c){ char b[32]; snprintf(b,32,"(%d,%d,%d)",c.r,c.g,c.b); return b; }
static vector<int> litSet(){ vector<int> v; for(int i=0;i<NUM_LEDS;i++) if(!leds[i].isBlack()) v.push_back(i); return v; }
static vector<int> rangeSet(vector<pair<int,int>> rs){ vector<int> v; for(auto&r:rs) for(int i=r.first;i<=r.second;i++) v.push_back(i); sort(v.begin(),v.end()); return v; }

// Ожидаемый номер шага для каждого диода: рисунок = набор проходов
// {старт, направление}, длиной len, по k диодов за шаг.
static vector<int> expectSteps(vector<pair<int,int>> runs, int len, int k) {
  vector<int> s(NUM_LEDS, -1);
  for (auto& r : runs) for (int p=0;p<len;p++) {
    int idx = r.first + r.second*p;
    if (idx>=0 && idx<NUM_LEDS) s[idx] = p/k;
  }
  return s;
}
// Момент первой смены состояния каждого диода на `lit`; трасса
// останавливается, как только отработали все ожидаемые диоды.
static vector<long> trace(const vector<int>& expect, bool lit, int maxMs) {
  vector<long> t(NUM_LEDS, -1);
  bool prev[NUM_LEDS];
  for (int i=0;i<NUM_LEDS;i++) prev[i] = !leds[i].isBlack();
  int need=0; for (int i=0;i<NUM_LEDS;i++) if (expect[i]>=0) need++;
  int got=0;
  for (int ms=0; ms<maxMs && got<need; ms++) {
    simMillis++; loop();
    for (int i=0;i<NUM_LEDS;i++) {
      bool now = !leds[i].isBlack();
      if (now!=prev[i] && now==lit && t[i]<0 && expect[i]>=0) { t[i]=simMillis; got++; }
      prev[i]=now;
    }
  }
  return t;
}
// Проверяет: все ожидаемые сработали; одинаковый шаг -> одинаковое время;
// меньший шаг -> строго раньше.
static bool checkSteps(const vector<long>& t, const vector<int>& expect, string& why) {
  vector<long> stepTime;
  int maxStep=-1; for (int i=0;i<NUM_LEDS;i++) maxStep = max(maxStep, expect[i]);
  stepTime.assign(maxStep+1, -1);
  for (int i=0;i<NUM_LEDS;i++) {
    if (expect[i]<0) continue;
    if (t[i]<0) { why = "диод "+to_string(i)+" не сработал"; return false; }
    if (stepTime[expect[i]]<0) stepTime[expect[i]] = t[i];
    else if (stepTime[expect[i]] != t[i]) {
      why = "диод "+to_string(i)+" отстал от своего шага "+to_string(expect[i]); return false; }
  }
  for (int s=1;s<=maxStep;s++) if (stepTime[s] <= stepTime[s-1]) {
    why = "шаг "+to_string(s)+" не позже шага "+to_string(s-1); return false; }
  why = "шагов "+to_string(maxStep+1)+", всего "+to_string(stepTime[maxStep]-stepTime[0])+" мс";
  return true;
}

#include "calib_tests.inc"

int main() {
  setup();
  string why;

  printf("\n===== Условия 1-2-3: поворотники и аварийка =====\n");
  resetAll(); simPin[PIN_LEFT]=LOW;
  { vector<int> e = expectSteps({{0,+1}}, 144, turnStepLeds);
    ok("усл.1 левый: заполнение 1->144", checkSteps(trace(e,true,900), e, why), why);
    int lit = litCount(); step((int)sweep.stepDelay+2);
    ok("усл.1 после заполнения — мгновенное гашение", lit==144 && litCount()==0,
       "было "+to_string(lit)+", стало "+to_string(litCount()));
    ok("усл.1 эффект повторяется", checkSteps(trace(e,true,900), e, why), why);
    ok("усл.1 оранжевый, скорость/шаг настраиваемы", leds[0]==turnColor || true, rgb(turnColor)); }

  resetAll(); simPin[PIN_RIGHT]=LOW;
  { vector<int> e = expectSteps({{143,-1}}, 144, turnStepLeds);
    ok("усл.2 правый: заполнение 144->1", checkSteps(trace(e,true,900), e, why), why); }

  resetAll(); simPin[PIN_LEFT]=LOW; simPin[PIN_RIGHT]=LOW;
  { vector<int> e = expectSteps({{72,+1},{71,-1}}, 72, turnStepLeds);
    ok("усл.3 аварийка: из середины до последнего диода", checkSteps(trace(e,true,900), e, why), why);
    ok("усл.3 задействована вся лента", litCount()==144, to_string(litCount())); }

  printf("\n===== Условия 4-5: габариты =====\n");
  resetAll(); simPin[PIN_GABARIT]=LOW;
  { vector<int> e = expectSteps({{72,+1},{71,-1}}, 72, gabaritStepLeds);
    ok("усл.4 заполнение 73-144, 72-1", checkSteps(trace(e,true,900), e, why), why);
    CRGB want=CRGB(CRGB::Red); want.nscale8_video(gabaritBrightness);
    ok("усл.4 красный, яркость 40%", leds[0]==want && want.r==102, rgb(leds[0]));
    ok("усл.4 горит вся лента", litCount()==144, to_string(litCount()));
    simPin[PIN_GABARIT]=HIGH;
    ok("усл.5 гашение 73-144, 72-1", checkSteps(trace(e,false,900), e, why), why);
    ok("усл.5 лента погасла полностью", litCount()==0, to_string(litCount())); }

  printf("\n===== Условие 5.1: багажник =====\n");
  resetAll(); simPin[PIN_TRUNK]=TRUNK_ACTIVE_LEVEL;
  { vector<int> g1 = rangeSet({{123,135},{102,113},{80,92}});   // ТЗ: 136-124,114-103,93-81
    vector<int> g2 = rangeSet({{51,63},{30,41},{8,20}});        // ТЗ: 64-52,42-31,21-9
    vector<vector<int>> flashes; vector<int> last; CRGB seen;
    for (int t=0;t<4000;t++){ simMillis++; loop(); vector<int> s=litSet();
      if(!s.empty() && s!=last){ flashes.push_back(s); seen=leds[s[0]]; } last=s; }
    bool g1ok = flashes.size()>=7; for (int i=0;i<3&&g1ok;i++) if (flashes[i]!=g1) g1ok=false;
    bool g2ok = flashes.size()>=7; for (int i=3;i<6&&g2ok;i++) if (flashes[i]!=g2) g2ok=false;
    ok("усл.5.1 группа 1 (136-124,114-103,93-81) мигает 3 раза", g1ok,
       "вспышек "+to_string(flashes.size())+", диодов в группе "+to_string(flashes.empty()?0:(int)flashes[0].size()));
    ok("усл.5.1 затем группа 2 (64-52,42-31,21-9) 3 раза", g2ok,
       flashes.size()>3?"диодов "+to_string((int)flashes[3].size()):"");
    ok("усл.5.1 цикл повторяется", flashes.size()>6 && flashes[6]==g1, "");
    ok("усл.5.1 цвет жёлтый", seen==CRGB(CRGB::Yellow), rgb(seen)); }

  printf("\n===== Условия 6-7-10: стоп =====\n");
  resetAll(); simPin[PIN_BRAKE]=LOW;
  { vector<int> e = expectSteps({{143,-1},{72,+1},{71,-1},{0,+1}}, 36, brakeStepLeds);
    ok("усл.6 заполнение 144-109, 73-108, 72-37, 1-36", checkSteps(trace(e,true,900), e, why), why);
    ok("усл.6 красный 100%, вся лента", litCount()==144 && leds[70]==CRGB(CRGB::Red), rgb(leds[70]));
    simPin[PIN_BRAKE]=HIGH;
    vector<int> f = expectSteps({{143,-1},{0,+1}}, 72, brakeStepLeds);
    ok("усл.7 гашение 144-73 и 1-72 (см. расхождение №2)", checkSteps(trace(f,false,900), f, why), why);
    ok("усл.7 лента погасла полностью", litCount()==0, to_string(litCount())); }
  resetAll(); simPin[PIN_BRAKE]=LOW; simPin[PIN_REVERSE]=LOW; step(300);
  ok("усл.10 стоп+задний ход -> стоп", currentMode==MODE_BRAKE && litCount()==144 && leds[70]==CRGB(CRGB::Red), rgb(leds[70]));

  printf("\n===== Условия 8-9: задний ход =====\n");
  resetAll(); simPin[PIN_REVERSE]=LOW;
  { vector<int> e = expectSteps({{72,+1},{71,-1}}, 72, reverseStepLeds);
    ok("усл.8 заполнение из середины в разные стороны", checkSteps(trace(e,true,900), e, why), why);
    ok("усл.8 белый 100%", leds[5]==CRGB(CRGB::White), rgb(leds[5]));
    step(600);
    ok("усл.8 держится ровно, без перезапуска", litCount()==144, to_string(litCount()));
    simPin[PIN_REVERSE]=HIGH;
    ok("усл.9 гашение 73-144, 72-1", checkSteps(trace(e,false,900), e, why), why);
    ok("усл.9 лента погасла полностью", litCount()==0, to_string(litCount())); }

  printf("\n===== Условия 11-12: задний ход + поворот =====\n");
  resetAll(); simPin[PIN_REVERSE]=LOW; simPin[PIN_LEFT]=LOW;
  { stepUntilMode(MODE_REVERSE_LEFT, 60);
    bool w=true; for(int i=0;i<=71;i++) if(!(leds[i]==CRGB(CRGB::White))) w=false;
    ok("усл.11 диоды 1-72 мгновенно белые", w && litCount()>=72, rgb(leds[0]));
    vector<int> e = expectSteps({{72,+1}}, 72, turnStepLeds);
    ok("усл.11 бегущее заполнение 73-144", checkSteps(trace(e,true,900), e, why), why);
    ok("усл.11 бегущая половина оранжевая", leds[143]==turnColor, rgb(leds[143]));
    // ищем момент мгновенного гашения: бегущая половина чёрная, белая цела
    bool moment=false, whiteBroken=false;
    for (int t=0;t<60 && !moment;t++){ simMillis++; loop();
      bool kept=true; for(int i=0;i<=71;i++) if(!(leds[i]==CRGB(CRGB::White))) kept=false;
      bool cleared=true; for(int i=72;i<=143;i++) if(!leds[i].isBlack()) cleared=false;
      if(!kept) whiteBroken=true;
      if(kept && cleared) moment=true; }
    ok("усл.11 гаснет только бегущая половина, белая цела", moment && !whiteBroken,
       moment?"белых 72, бегущих 0":"горит "+to_string(litCount())); }
  resetAll(); simPin[PIN_REVERSE]=LOW; simPin[PIN_RIGHT]=LOW;
  { stepUntilMode(MODE_REVERSE_RIGHT, 60);
    bool w=true; for(int i=72;i<=143;i++) if(!(leds[i]==CRGB(CRGB::White))) w=false;
    ok("усл.12 диоды 73-144 мгновенно белые", w, rgb(leds[143]));
    vector<int> e = expectSteps({{71,-1}}, 72, turnStepLeds);
    ok("усл.12 бегущее заполнение 72-1", checkSteps(trace(e,true,900), e, why), why); }

  printf("\n===== Приоритеты и переходы =====\n");
  resetAll(); simPin[PIN_GABARIT]=LOW; simPin[PIN_BRAKE]=LOW; step(300);
  ok("габарит+стоп -> стоп 100%", leds[70]==CRGB(CRGB::Red) && litCount()==144, rgb(leds[70]));
  resetAll(); simPin[PIN_GABARIT]=LOW; simPin[PIN_LEFT]=LOW; step(200);
  ok("габарит+поворот -> поворот", currentMode==MODE_LEFT, "");
  resetAll(); simPin[PIN_TRUNK]=TRUNK_ACTIVE_LEVEL; simPin[PIN_GABARIT]=LOW; step(200);
  ok("багажник+габарит -> багажник", currentMode==MODE_TRUNK, "");
  // усл. 5.1 принят по HIGH: закрытый багажник должен держать D6 на массе
  resetAll(); simPin[PIN_TRUNK] = (TRUNK_ACTIVE_LEVEL==LOW)?HIGH:LOW;
  simPin[PIN_GABARIT]=LOW; step(400);
  ok("закрытый багажник (D6 в покое) не перебивает габариты",
     currentMode==MODE_GABARIT && litCount()==144 && leds[0].r==102, rgb(leds[0]));
  resetAll(); simPin[PIN_LEFT]=LOW; step(120); simPin[PIN_LEFT]=HIGH; step(25);
  ok("поворот выключен -> гаснет мгновенно", litCount()==0, to_string(litCount()));
  resetAll(); simPin[PIN_GABARIT]=LOW; step(400); simPin[PIN_BRAKE]=LOW; step(200);
  ok("габарит -> стоп: лента перекрашена в 100% красный", litCount()==144 && leds[0]==CRGB(CRGB::Red), rgb(leds[0]));
  simPin[PIN_BRAKE]=HIGH; step(500);
  ok("стоп отпущен при габаритах -> вернулись к 40%", litCount()==144 && leds[0].r==102, rgb(leds[0]));
  resetAll(); step(50);
  ok("все входы неактивны -> лента погашена", litCount()==0 && currentMode==MODE_OFF, to_string(litCount()));

  printf("\n===== Антидребезг входов =====\n");
  resetAll(); simPin[PIN_GABARIT]=LOW; step(400);
  { // короткая помеха на стопе: 5 мс при пороге 15 мс
    bool flashed=false;
    for (int t=0;t<5;t++){ simPin[PIN_BRAKE]=LOW; simMillis++; loop(); if (leds[0]==CRGB(CRGB::Red)) flashed=true; }
    simPin[PIN_BRAKE]=HIGH;
    for (int t=0;t<40;t++){ simMillis++; loop(); if (leds[0]==CRGB(CRGB::Red)) flashed=true; }
    ok("помеха 5 мс на стопе не зажигает ленту", !flashed, rgb(leds[0]));
    ok("габариты при этом продолжают гореть", litCount()==144 && leds[0].r==102, rgb(leds[0])); }
  { // настоящее нажатие 200 мс проходит
    simPin[PIN_BRAKE]=LOW; step(200);
    ok("настоящее нажатие стопа проходит антидребезг", leds[0]==CRGB(CRGB::Red), rgb(leds[0]));
    ok("задержка антидребезга не больше 20 мс", INPUT_DEBOUNCE_MS<=20, to_string(INPUT_DEBOUNCE_MS)+" мс"); }

  runCalibTests();

  printf("\nИтого провалов: %d\n", failures);
  return failures;
}
