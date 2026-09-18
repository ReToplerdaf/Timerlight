using System.Diagnostics;
using Microsoft.Win32;

namespace Timerlight;

/// <summary>
/// Owns the notification-area icon: it counts the current sitting, repaints the hourglass,
/// and reacts to clicks, idle time, lock and sleep.
/// </summary>
/// <remarks>
/// Two different spans are shown at once. The colour of the sand tracks the whole interval,
/// usually an hour, walking from green to red. The sand itself pours over a much shorter
/// stretch - ten minutes by default - and the glass is turned over each time that stretch
/// runs out, so the icon keeps moving instead of sitting still for an hour.
/// </remarks>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int MinuteMilliseconds = 60_000;

    // Everything is quantised to whole minutes, so one tick a minute is all the widget needs
    // to stay current. Blinking and the turn-over are the only things that ask for more.
    private const int BlinkIntervalMilliseconds = 600;

    // Ticks are aimed just past a minute boundary. Landing exactly on it would let
    // (int)Elapsed.TotalMinutes read one minute short and hold the sand back a whole minute.
    private const int TickGuardMilliseconds = 50;
    private const int FlipFrameIntervalMilliseconds = 35;
    private const int FlipFrameCount = 14;

    private const float BlinkDimOpacity = 0.22f;
    private const int MaxTooltipLength = 63;

    private static readonly int[] IntervalPresets = [15, 30, 45, 60, 90, 120];
    private static readonly int[] FlipPresets = [0, 5, 10, 15, 20, 30];
    private static readonly int[] IdlePresets = [0, 5, 10, 15, 30];
    private static readonly int[] AutoResetPresets = [0, 3, 5, 10, 15];
    private static readonly TimeSpan ThemeRefreshInterval = TimeSpan.FromSeconds(20);

    private readonly AppSettings _settings;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _flipTimer;
    private readonly Stopwatch _sitting = new();

    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _intervalItem;
    private readonly ToolStripMenuItem _flipItem;
    private readonly ToolStripMenuItem _idleItem;
    private readonly ToolStripMenuItem _autoResetItem;
    private readonly ToolStripMenuItem _blinkItem;
    private readonly ToolStripMenuItem _notificationItem;
    private readonly ToolStripMenuItem _resetOnUnlockItem;
    private readonly ToolStripMenuItem _resetOnResumeItem;
    private readonly ToolStripMenuItem _autostartItem;

    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;

    // The previous icon is kept alive for one update so the shell is never left
    // holding a handle that has just been freed.
    private Icon? _retiredIcon;
    private IntPtr _retiredIconHandle;

    private string _renderedSignature = string.Empty;
    private bool _blinkVisible = true;
    private bool _targetAnnounced;
    private bool _lightBackground = true;
    private DateTime _themeCheckedAt = DateTime.MinValue;
    private int _lastSegment;
    private int _flipFrame;
    private bool _disposed;

    internal TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause()) { CheckOnClick = false };
        _intervalItem = new ToolStripMenuItem("Интервал");
        _flipItem = new ToolStripMenuItem("Переворот часов");
        _idleItem = new ToolStripMenuItem("Сброс при бездействии");
        _autoResetItem = new ToolStripMenuItem("Автосброс после сигнала");
        _blinkItem = new ToolStripMenuItem("Мигать по достижении", null, (_, _) => ToggleBlink());
        _notificationItem = new ToolStripMenuItem("Показывать уведомление", null, (_, _) => ToggleNotification());
        _resetOnUnlockItem = new ToolStripMenuItem("Сбрасывать после разблокировки", null, (_, _) => ToggleResetOnUnlock());
        _resetOnResumeItem = new ToolStripMenuItem("Сбрасывать после сна", null, (_, _) => ToggleResetOnResume());
        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => ToggleAutostart());

        BuildIntervalMenu();
        BuildFlipMenu();
        BuildIdleMenu();
        BuildAutoResetMenu();

        var resetItem = new ToolStripMenuItem("Сбросить таймер", null, (_, _) => ResetSitting())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            resetItem,
            _pauseItem,
            new ToolStripSeparator(),
            _intervalItem,
            _flipItem,
            _idleItem,
            _autoResetItem,
            new ToolStripSeparator(),
            _blinkItem,
            _notificationItem,
            _resetOnUnlockItem,
            _resetOnResumeItem,
            _autostartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("О программе", null, (_, _) => ShowAbout()),
            new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication()),
        });
        _menu.Opening += (_, _) => RefreshMenuState();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "Timerlight",
            Visible = true,
        };
        _notifyIcon.MouseClick += OnTrayIconClicked;
        _notifyIcon.BalloonTipClicked += (_, _) => ResetSitting();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _timer = new System.Windows.Forms.Timer { Interval = MinuteMilliseconds };
        _timer.Tick += (_, _) => Refresh();

        _flipTimer = new System.Windows.Forms.Timer { Interval = FlipFrameIntervalMilliseconds };
        _flipTimer.Tick += (_, _) => OnFlipTick();

        _sitting.Start();
        RefreshTheme(force: true);
        Refresh();
        _timer.Start();
    }

    private bool IsPaused => !_sitting.IsRunning;

    private TimeSpan Elapsed => _sitting.Elapsed;

    /// <summary>Whole minutes of the current sitting. Ticks land just after a minute rolls over.</summary>
    private int ElapsedMinutes => (int)Elapsed.TotalMinutes;

    private int TargetMinutes => Math.Max(_settings.TargetMinutes, AppSettings.MinTargetMinutes);

    private TimeSpan Target => TimeSpan.FromMinutes(TargetMinutes);

    private bool FlipEnabled => _settings.FlipMinutes > 0;

    /// <summary>How long one pour lasts before the glass is turned over.</summary>
    private int SegmentMinutes => FlipEnabled
        ? Math.Min(_settings.FlipMinutes, TargetMinutes)
        : TargetMinutes;

    /// <summary>Drives the colour: how far the whole interval has got.</summary>
    private double ColorProgress => Math.Clamp(ElapsedMinutes / (double)TargetMinutes, 0d, 1d);

    private bool IsFinished => ElapsedMinutes >= TargetMinutes;

    /// <summary>Drives the sand level: how far the current pour has got.</summary>
    private double SandProgress
    {
        get
        {
            if (IsFinished)
            {
                // Nothing left to pour - the glass stays drained while it blinks.
                return 1d;
            }

            int segment = SegmentMinutes;
            return (ElapsedMinutes % segment) / (double)segment;
        }
    }

    /// <summary>Whether the finished interval restarts by itself after the break.</summary>
    private bool AutoResetEnabled => _settings.AutoResetMinutes > 0;

    /// <summary>Whole minutes left of the break before the count restarts on its own.</summary>
    private int AutoResetMinutesLeft =>
        Math.Max(0, TargetMinutes + _settings.AutoResetMinutes - ElapsedMinutes);

    /// <summary>One pass: apply the idle rule, turn the glass over if due, repaint and relabel.</summary>
    private void Refresh()
    {
        ApplyIdleRule();
        ApplyAutoResetRule();
        RefreshTheme(force: false);

        bool finished = IsFinished;
        int segment = ElapsedMinutes / SegmentMinutes;

        if (finished && !IsPaused && !_targetAnnounced)
        {
            _targetAnnounced = true;
            if (_settings.ShowNotification)
            {
                AnnounceTarget();
            }
        }

        bool turnOver = FlipEnabled && !finished && !IsPaused && segment != _lastSegment;
        _lastSegment = segment;

        if (turnOver)
        {
            StartFlip();
            UpdateTooltip(finished);
            ScheduleNextTick(blinking: false);
            return;
        }

        bool shouldBlink = finished && !IsPaused && _settings.BlinkWhenFinished;
        _blinkVisible = shouldBlink ? !_blinkVisible : true;

        if (!_flipTimer.Enabled)
        {
            UpdateIcon(SandProgress, ColorProgress, finished, flipAngle: 0d);
        }

        UpdateTooltip(finished);
        ScheduleNextTick(shouldBlink);
    }

    /// <summary>
    /// Picks when to wake up next. Normally that is the next whole minute of the sitting, so the
    /// sand level and the turn-over happen on the minute instead of drifting with the timer.
    /// </summary>
    private void ScheduleNextTick(bool blinking)
    {
        int interval;
        if (blinking)
        {
            interval = BlinkIntervalMilliseconds;
        }
        else if (IsPaused)
        {
            interval = MinuteMilliseconds;
        }
        else
        {
            double elapsed = Elapsed.TotalMilliseconds;
            double nextMinute = (Math.Floor(elapsed / MinuteMilliseconds) + 1d) * MinuteMilliseconds;
            interval = (int)Math.Clamp(
                nextMinute - elapsed + TickGuardMilliseconds,
                TickGuardMilliseconds,
                MinuteMilliseconds + TickGuardMilliseconds);
        }

        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    private void StartFlip()
    {
        _flipFrame = 0;
        _flipTimer.Start();
        DrawFlipFrame();
    }

    private void OnFlipTick()
    {
        _flipFrame++;
        if (_flipFrame >= FlipFrameCount)
        {
            _flipTimer.Stop();
            _flipFrame = 0;
            _renderedSignature = string.Empty;
            Refresh();
            return;
        }

        DrawFlipFrame();
    }

    /// <summary>
    /// Throughout the turn the glass is drawn drained - empty on top, full below. Turned a full
    /// half circle that picture is exactly a fresh pour, so the animation joins the next segment
    /// without a jump.
    /// </summary>
    private void DrawFlipFrame()
    {
        double angle = 180d * _flipFrame / FlipFrameCount;
        UpdateIcon(sandProgress: 1d, colorProgress: ColorProgress, finished: false, flipAngle: angle);
    }

    private void StopFlip()
    {
        _flipTimer.Stop();
        _flipFrame = 0;
    }

    /// <summary>
    /// Ends the break on its own. The icon signals for as long as the break is meant to last
    /// and then starts the next interval, so a sitting that is simply ignored still rolls
    /// over instead of blinking all afternoon. Clicking the icon cuts the break short.
    /// </summary>
    private void ApplyAutoResetRule()
    {
        if (!AutoResetEnabled || IsPaused)
        {
            return;
        }

        if (ElapsedMinutes >= TargetMinutes + _settings.AutoResetMinutes)
        {
            ResetSittingCore();
        }
    }

    private void ApplyIdleRule()
    {
        if (_settings.IdleResetMinutes <= 0 || IsPaused)
        {
            return;
        }

        // While the user stays away the timer is held at zero, so the hour starts
        // counting again from the moment they come back.
        if (NativeMethods.GetIdleTime().TotalMinutes >= _settings.IdleResetMinutes)
        {
            ResetSittingCore();
        }
    }

    private void UpdateIcon(double sandProgress, double colorProgress, bool finished, double flipAngle)
    {
        int size = TrayIconSize();

        // Quantising keeps the redraw from running when nothing would change on screen.
        int sandStep = (int)Math.Round(sandProgress * 200d);
        int colorStep = (int)Math.Round(colorProgress * 200d);
        int angleStep = (int)Math.Round(flipAngle);
        string signature =
            $"{size}|{sandStep}|{colorStep}|{angleStep}|{finished}|{IsPaused}|{_lightBackground}|{_blinkVisible}";
        if (signature == _renderedSignature && _currentIcon is not null)
        {
            return;
        }

        _renderedSignature = signature;

        var state = new HourglassState(
            SandProgress: sandProgress,
            ColorProgress: colorProgress,
            FlipAngle: flipAngle,
            Finished: finished,
            Paused: IsPaused,
            LightBackground: _lightBackground,
            Opacity: _blinkVisible ? 1f : BlinkDimOpacity);

        using Bitmap bitmap = HourglassIconRenderer.Render(state, size);

        IntPtr handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);

        ReleaseRetiredIcon();
        _retiredIcon = _currentIcon;
        _retiredIconHandle = _currentIconHandle;

        _currentIcon = icon;
        _currentIconHandle = handle;
        _notifyIcon.Icon = icon;
    }

    /// <summary>
    /// Frees the icon that was replaced one update ago. Icon.FromHandle does not take
    /// ownership, so the HICON has to be destroyed by hand or the process leaks GDI handles.
    /// </summary>
    private void ReleaseRetiredIcon()
    {
        _retiredIcon?.Dispose();
        _retiredIcon = null;

        if (_retiredIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_retiredIconHandle);
            _retiredIconHandle = IntPtr.Zero;
        }
    }

    private void UpdateTooltip(bool finished)
    {
        string text;
        if (IsPaused)
        {
            text = $"Пауза · {FormatDuration(Elapsed)} из {FormatDuration(Target)}";
        }
        else if (finished && AutoResetEnabled)
        {
            text = $"Пора размяться · сброс через {FormatDuration(TimeSpan.FromMinutes(AutoResetMinutesLeft))}";
        }
        else if (finished)
        {
            TimeSpan over = Elapsed - Target;
            text = over.TotalMinutes >= 1
                ? $"Пора размяться · {FormatDuration(Elapsed)} (+{FormatDuration(over)})"
                : $"Пора размяться · {FormatDuration(Elapsed)}";
        }
        else
        {
            text = $"За компом {FormatDuration(Elapsed)} из {FormatDuration(Target)}";
        }

        if (text.Length > MaxTooltipLength)
        {
            text = text[..MaxTooltipLength];
        }

        if (_notifyIcon.Text != text)
        {
            _notifyIcon.Text = text;
        }
    }

    private void AnnounceTarget()
    {
        string advice = AutoResetEnabled
            ? $"Пора размяться — отсчёт начнётся заново через {FormatDuration(TimeSpan.FromMinutes(_settings.AutoResetMinutes))} или сразу по щелчку значка."
            : "Пора размяться — щёлкните по значку, чтобы начать отсчёт заново.";

        _notifyIcon.ShowBalloonTip(
            10_000,
            "Timerlight",
            $"Вы за компьютером уже {FormatDuration(Elapsed)}. {advice}",
            ToolTipIcon.Info);
    }

    private void OnTrayIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ResetSitting();
        }
    }

    private void ResetSitting()
    {
        ResetSittingCore();
        Refresh();
    }

    /// <summary>
    /// Zeroes the count without repainting. Callers that already run inside
    /// <see cref="Refresh"/> use this, otherwise the two would call each other forever.
    /// </summary>
    private void ResetSittingCore()
    {
        bool wasPaused = IsPaused;
        _sitting.Reset();
        if (!wasPaused)
        {
            _sitting.Start();
        }

        StopFlip();
        _lastSegment = 0;
        _targetAnnounced = false;
        _blinkVisible = true;
        _renderedSignature = string.Empty;
    }

    private void TogglePause()
    {
        if (IsPaused)
        {
            _sitting.Start();
        }
        else
        {
            _sitting.Stop();
        }

        StopFlip();
        _renderedSignature = string.Empty;
        Refresh();
    }

    private void ToggleBlink()
    {
        _settings.BlinkWhenFinished = !_settings.BlinkWhenFinished;
        _settings.Save();
        Refresh();
    }

    private void ToggleNotification()
    {
        _settings.ShowNotification = !_settings.ShowNotification;
        _settings.Save();
    }

    private void ToggleResetOnUnlock()
    {
        _settings.ResetOnUnlock = !_settings.ResetOnUnlock;
        _settings.Save();
    }

    private void ToggleResetOnResume()
    {
        _settings.ResetOnResume = !_settings.ResetOnResume;
        _settings.Save();
    }

    private void ToggleAutostart()
    {
        bool desired = !Autostart.IsEnabled();
        if (!Autostart.SetEnabled(desired))
        {
            MessageBox.Show(
                "Не удалось изменить автозапуск: Windows отклонила запись в реестр.",
                "Timerlight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void BuildIntervalMenu()
    {
        foreach (int minutes in IntervalPresets)
        {
            _intervalItem.DropDownItems.Add(new ToolStripMenuItem(
                FormatDuration(TimeSpan.FromMinutes(minutes)),
                null,
                (sender, _) => ApplyInterval((int)((ToolStripMenuItem)sender!).Tag!))
            {
                Tag = minutes,
            });
        }

        _intervalItem.DropDownItems.Add(new ToolStripSeparator());
        _intervalItem.DropDownItems.Add(new ToolStripMenuItem("Свой интервал…", null, (_, _) => AskForInterval()));
    }

    private void BuildFlipMenu()
    {
        foreach (int minutes in FlipPresets)
        {
            _flipItem.DropDownItems.Add(new ToolStripMenuItem(
                minutes == 0 ? "Без переворота" : $"Каждые {FormatDuration(TimeSpan.FromMinutes(minutes))}",
                null,
                (sender, _) => ApplyFlipInterval((int)((ToolStripMenuItem)sender!).Tag!))
            {
                Tag = minutes,
            });
        }
    }

    private void BuildIdleMenu()
    {
        foreach (int minutes in IdlePresets)
        {
            _idleItem.DropDownItems.Add(new ToolStripMenuItem(
                minutes == 0 ? "Выключен" : $"Через {FormatDuration(TimeSpan.FromMinutes(minutes))}",
                null,
                (sender, _) => ApplyIdleReset((int)((ToolStripMenuItem)sender!).Tag!))
            {
                Tag = minutes,
            });
        }
    }

    private void BuildAutoResetMenu()
    {
        foreach (int minutes in AutoResetPresets)
        {
            _autoResetItem.DropDownItems.Add(new ToolStripMenuItem(
                minutes == 0 ? "Только по щелчку" : $"Через {FormatDuration(TimeSpan.FromMinutes(minutes))}",
                null,
                (sender, _) => ApplyAutoReset((int)((ToolStripMenuItem)sender!).Tag!))
            {
                Tag = minutes,
            });
        }
    }

    private void ApplyInterval(int minutes)
    {
        _settings.TargetMinutes = Math.Clamp(minutes, AppSettings.MinTargetMinutes, AppSettings.MaxTargetMinutes);
        _settings.Save();
        _targetAnnounced = false;
        ResyncSegment();
        Refresh();
    }

    private void ApplyFlipInterval(int minutes)
    {
        _settings.FlipMinutes = Math.Clamp(minutes, 0, AppSettings.MaxTargetMinutes);
        _settings.Save();
        ResyncSegment();
        Refresh();
    }

    /// <summary>
    /// Re-reads which pour the sitting is in. Without this, changing an interval would look
    /// like a segment boundary and set off a turn-over that is not due.
    /// </summary>
    private void ResyncSegment()
    {
        StopFlip();
        _lastSegment = ElapsedMinutes / SegmentMinutes;
        _renderedSignature = string.Empty;
    }

    private void ApplyIdleReset(int minutes)
    {
        _settings.IdleResetMinutes = Math.Max(0, minutes);
        _settings.Save();
    }

    private void ApplyAutoReset(int minutes)
    {
        _settings.AutoResetMinutes = Math.Clamp(minutes, 0, AppSettings.MaxTargetMinutes);
        _settings.Save();

        // A shorter break may already be over, and the tooltip counts down either way.
        _renderedSignature = string.Empty;
        Refresh();
    }

    private void AskForInterval()
    {
        using var dialog = new CustomIntervalForm(_settings.TargetMinutes);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            ApplyInterval(dialog.Minutes);
        }
    }

    private void RefreshMenuState()
    {
        _pauseItem.Checked = IsPaused;
        _blinkItem.Checked = _settings.BlinkWhenFinished;
        _notificationItem.Checked = _settings.ShowNotification;
        _resetOnUnlockItem.Checked = _settings.ResetOnUnlock;
        _resetOnResumeItem.Checked = _settings.ResetOnResume;
        _autostartItem.Checked = Autostart.IsEnabled();

        _intervalItem.Text = $"Интервал: {FormatDuration(Target)}";
        CheckPreset(_intervalItem, _settings.TargetMinutes);

        _flipItem.Text = FlipEnabled
            ? $"Переворот часов: {FormatDuration(TimeSpan.FromMinutes(SegmentMinutes))}"
            : "Переворот часов: выключен";
        CheckPreset(_flipItem, _settings.FlipMinutes);

        _idleItem.Text = _settings.IdleResetMinutes > 0
            ? $"Сброс при бездействии: {FormatDuration(TimeSpan.FromMinutes(_settings.IdleResetMinutes))}"
            : "Сброс при бездействии: выключен";
        CheckPreset(_idleItem, _settings.IdleResetMinutes);

        _autoResetItem.Text = AutoResetEnabled
            ? $"Автосброс после сигнала: {FormatDuration(TimeSpan.FromMinutes(_settings.AutoResetMinutes))}"
            : "Автосброс после сигнала: только по щелчку";
        CheckPreset(_autoResetItem, _settings.AutoResetMinutes);
    }

    private static void CheckPreset(ToolStripMenuItem parent, int selectedMinutes)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is int minutes)
            {
                menuItem.Checked = minutes == selectedMinutes;
            }
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"""
             Timerlight {typeof(TrayApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "0.2.0"}

             Песочные часы в трее показывают, сколько вы сидите за компьютером.
             Цвет песка идёт от зелёного к красному за весь интервал, сам песок
             пересыпается за {FormatDuration(TimeSpan.FromMinutes(SegmentMinutes))}, после чего часы переворачиваются.
             В конце интервала значок мигает.

             Щелчок левой кнопкой по значку — начать отсчёт заново.

             Настройки: {AppSettings.FilePath}
             """,
            "Timerlight",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_settings.ResetOnUnlock &&
            e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            ResetSitting();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_settings.ResetOnResume && e.Mode == PowerModes.Resume)
        {
            ResetSitting();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color or UserPreferenceCategory.Window)
        {
            RefreshTheme(force: true);
        }
    }

    private void RefreshTheme(bool force)
    {
        if (!force && DateTime.UtcNow - _themeCheckedAt < ThemeRefreshInterval)
        {
            return;
        }

        _themeCheckedAt = DateTime.UtcNow;
        _lightBackground = IsTaskbarLight();
    }

    /// <summary>Reads the Windows setting that drives the taskbar colour.</summary>
    private static bool IsTaskbarLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static int TrayIconSize()
    {
        int size = SystemInformation.SmallIconSize.Width;
        return size <= 0 ? 16 : Math.Clamp(size, 16, 64);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int totalMinutes = Math.Max(0, (int)duration.TotalMinutes);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;

        if (hours == 0)
        {
            return $"{minutes} мин";
        }

        return minutes == 0 ? $"{hours} ч" : $"{hours} ч {minutes} мин";
    }

    private void ExitApplication()
    {
        _settings.Save();

        // Remove the icon immediately rather than leaving a ghost until the tray is hovered.
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            _flipTimer.Stop();
            _flipTimer.Dispose();
            _timer.Stop();
            _timer.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();

            ReleaseRetiredIcon();

            _currentIcon?.Dispose();
            _currentIcon = null;
            if (_currentIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_currentIconHandle);
                _currentIconHandle = IntPtr.Zero;
            }
        }

        base.Dispose(disposing);
    }
}
