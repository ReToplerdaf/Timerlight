// ---------- Условие 13 ----------
static unsigned long calT0=0, calPeriod=700, calOn=350; static int calCount=8;
static void pulseHook(unsigned long t) {
  if (t < calT0) { simPin[PIN_LEFT]=HIGH; return; }
  unsigned long d = t - calT0, n = d/calPeriod, ph = d%calPeriod;
  simPin[PIN_LEFT] = ((long)n < calCount && ph < calOn) ? LOW : HIGH;
}
struct Seg { unsigned long t0, t1; uint8_t r,g,b; };
static vector<Seg> segments() {
  vector<Seg> v;
  for (int i=0;i<simShowLogN;i++) {
    Seg s; s.t0=simShowLog[i].t; s.r=simShowLog[i].r; s.g=simShowLog[i].g; s.b=simShowLog[i].b;
    s.t1 = (i+1<simShowLogN) ? simShowLog[i+1].t : s.t0;
    v.push_back(s);
  }
  return v;
}
static int countSeg(const vector<Seg>& v, uint8_t r, uint8_t g, uint8_t b) {
  int c=0; for (auto&s:v) if (s.r==r&&s.g==g&&s.b==b) c++; return c;
}
static void runCalibTests() {
  string why;
  printf("\n===== Условие 13: калибровка =====\n");

  // --- вход по удержанию D2 4 секунды
  resetAll();
  simPin[PIN_CALIBRATE]=LOW; simPin[PIN_GABARIT]=LOW;
  bool fired=false; int litDuringHold=0;
  for (int t=0;t<3500;t++){ simMillis++; if (calibrationRequested()) fired=true; else loop(); }
  litDuringHold = litCount();
  ok("усл.13 до 4 секунд в калибровку не уходим", !fired, "");
  ok("усл.13 пока кнопка держится, эффекты работают", litDuringHold==144, to_string(litDuringHold));
  for (int t=0;t<800 && !fired;t++){ simMillis++; if (calibrationRequested()) fired=true; else loop(); }
  ok("усл.13 удержание 4 с -> вход в калибровку", fired, "");
  simPin[PIN_CALIBRATE]=HIGH;

  // --- измерение импульсов 350/700
  resetAll(); memset(simEeprom, 0xFF, sizeof(simEeprom));
  calib.onMs=0; calib.periodMs=0; turnFillMs=350; turnStepLeds=3;
  simShowLogN=0; simAutoTick=1;
  calT0=simMillis+800; calPeriod=700; calOn=350; calCount=8; simPinHook=pulseHook;
  runCalibrationMode();
  simPinHook=0; simAutoTick=0; allInactive();
  vector<Seg> segs = segments();
  ok("усл.13 длительность импульса измерена (350 мс)",
     calib.onMs>325 && calib.onMs<375, to_string(calib.onMs)+" мс");
  ok("усл.13 интервал между импульсами измерен (700 мс)",
     calib.periodMs>670 && calib.periodMs<730, to_string(calib.periodMs)+" мс");
  ok("усл.13 мигание зелёным до первого сигнала", countSeg(segs,0,128,0)>=1,
     to_string(countSeg(segs,0,128,0))+" вспышек");
  ok("усл.13 синяя вспышка на каждый из 4 сигналов", countSeg(segs,0,0,255)==4,
     to_string(countSeg(segs,0,0,255)));
  { bool durOk=true; string d;
    for (auto&s:segs) if (s.r==0&&s.g==0&&s.b==255) { long len=(long)(s.t1-s.t0);
      d+=to_string(len)+" "; if (len<325||len>395) durOk=false; }
    ok("усл.13 длина синей вспышки = длине импульса", durOk, d); }
  ok("усл.13 пять белых вспышек в конце", countSeg(segs,255,255,255)==5,
     to_string(countSeg(segs,255,255,255)));
  { long wt=0; bool first=true; unsigned long a=0,b=0;
    for (auto&s:segs) if (s.r==255&&s.g==255&&s.b==255){ if(first){a=s.t0;first=false;} b=s.t1; }
    wt=(long)(b-a);
    ok("усл.13 белые вспышки укладываются в ~2 секунды", wt>1700 && wt<2400, to_string(wt)+" мс"); }
  ok("усл.13 время заполнения пересчитано (90% импульса)",
     turnFillMs>290 && turnFillMs<340, to_string(turnFillMs)+" мс");
  ok("усл.13 шаг оставлен базовым при длинном импульсе", turnStepLeds==3, to_string(turnStepLeds));

  // --- скорость применилась к условиям 1/2/3/11/12
  resetAll(); simPin[PIN_LEFT]=LOW;
  { vector<int> e = expectSteps({{0,+1}}, 144, turnStepLeds);
    trace(e,true,900);
    long span=0; { vector<long> t=trace(e,true,900); span=t[143]-t[0]; }
    ok("усл.13 новая скорость применена к повороту (усл.1)",
       span>turnFillMs-40 && span<turnFillMs+40, to_string(span)+" мс при turnFillMs="+to_string(turnFillMs)); }

  // --- EEPROM переживает перезагрузку
  { uint16_t savedOn=calib.onMs, savedPer=calib.periodMs;
    calib.onMs=0; calib.periodMs=0; turnFillMs=350; turnStepLeds=3;
    loadCalibration();
    ok("усл.13 значения восстановлены из EEPROM",
       calib.onMs==savedOn && calib.periodMs==savedPer,
       to_string(calib.onMs)+"/"+to_string(calib.periodMs));
    ok("усл.13 скорость восстановлена из EEPROM после перезагрузки",
       turnFillMs>290 && turnFillMs<340, to_string(turnFillMs)); }

  // --- пустой EEPROM не ломает работу
  { memset(simEeprom, 0xFF, sizeof(simEeprom));
    calib.onMs=0; calib.periodMs=0; turnFillMs=350; turnStepLeds=3;
    loadCalibration();
    ok("усл.13 чистый EEPROM (0xFF) игнорируется", turnFillMs==350 && calib.onMs==0,
       to_string(turnFillMs)); }
  // --- испорченный EEPROM
  { memset(simEeprom, 0xFF, sizeof(simEeprom));
    CalibRecord bad = { CALIB_MAGIC, 400, 800, 1 };   // неверная контрольная сумма
    memcpy(simEeprom, &bad, sizeof(bad));
    calib.onMs=0; turnFillMs=350; loadCalibration();
    ok("усл.13 битая контрольная сумма отбрасывается", turnFillMs==350 && calib.onMs==0,
       to_string(turnFillMs)); }

  // --- короткий импульс: шаг увеличивается
  resetAll(); memset(simEeprom,0xFF,sizeof(simEeprom));
  calib.onMs=0; turnFillMs=350; turnStepLeds=3; turnStepLedsBase=3;
  simShowLogN=0; simAutoTick=1;
  calT0=simMillis+600; calPeriod=300; calOn=120; calCount=8; simPinHook=pulseHook;
  runCalibrationMode();
  simPinHook=0; simAutoTick=0; allInactive();
  ok("усл.13 короткий импульс (120 мс) измерен", calib.onMs>100 && calib.onMs<150, to_string(calib.onMs));
  ok("усл.13 при коротком импульсе шаг стал больше 3 диодов", turnStepLeds>3,
     to_string(turnStepLeds)+" диодов за шаг, заполнение "+to_string(turnFillMs)+" мс");
  { uint16_t steps=(NUM_LEDS+turnStepLeds-1)/turnStepLeds;
    ok("усл.13 шаг не короче времени вывода кадра", turnFillMs/steps>=TURN_MIN_STEP_MS,
       to_string(turnFillMs/steps)+" мс на шаг"); }

  // --- нет сигнала: отказ, старая калибровка цела
  { uint16_t before=calib.onMs; uint16_t fillBefore=turnFillMs;
    resetAll(); simAutoTick=1; simPinHook=0; simPin[PIN_LEFT]=HIGH; simShowLogN=0;
    unsigned long t0=simMillis;
    runCalibrationMode();
    simAutoTick=0;
    vector<Seg> s2=segments();
    ok("усл.13 нет сигнала -> выход по таймауту, без зависания",
       simMillis-t0 < CALIB_WAIT_MS+4000, to_string(simMillis-t0)+" мс");
    ok("усл.13 отказ показан красным", countSeg(s2,255,0,0)>=3, to_string(countSeg(s2,255,0,0)));
    ok("усл.13 прежняя калибровка сохранена", calib.onMs==before && turnFillMs==fillBefore,
       to_string(calib.onMs)); }

  // --- нажатие на тормоз прерывает калибровку
  { resetAll(); simAutoTick=1; simPin[PIN_BRAKE]=LOW; simShowLogN=0;
    unsigned long t0=simMillis; runCalibrationMode(); simAutoTick=0;
    ok("усл.13 тормоз прерывает калибровку сразу", simMillis-t0 < 3000, to_string(simMillis-t0)+" мс");
    allInactive(); step(200);
    ok("усл.13 после калибровки обычная логика работает", currentMode==MODE_OFF && litCount()==0, ""); }
}
