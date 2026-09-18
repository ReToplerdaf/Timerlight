using System.Diagnostics;
using Microsoft.Win32;

namespace Timerlight;

/// <summary>
/// Owns the notification-area icon: it counts the current sitting, repaints the hourglass,
/// and reacts to clicks, idle time, lock and sleep.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int TickIntervalMilliseconds = 500;
    private const float BlinkDimOpacity = 0.22f;
    private const int MaxTooltipLength = 63;

    private static readonly int[] IntervalPresets = [15, 30, 45, 60, 90, 120];
    private static readonly int[] IdlePresets = [0, 5, 10, 15, 30];
    private static readonly TimeSpan ThemeRefreshInterval = TimeSpan.FromSeconds(20);

    private readonly AppSettings _settings;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _sitting = new();

    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _intervalItem;
    private readonly ToolStripMenuItem _idleItem;
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
    private bool _disposed;

    internal TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause()) { CheckOnClick = false };
        _intervalItem = new ToolStripMenuItem("Интервал");
        _idleItem = new ToolStripMenuItem("Сброс при бездействии");
        _blinkItem = new ToolStripMenuItem("Мигать по достижении", null, (_, _) => ToggleBlink());
        _notificationItem = new ToolStripMenuItem("Показывать уведомление", null, (_, _) => ToggleNotification());
        _resetOnUnlockItem = new ToolStripMenuItem("Сбрасывать после разблокировки", null, (_, _) => ToggleResetOnUnlock());
        _resetOnResumeItem = new ToolStripMenuItem("Сбрасывать после сна", null, (_, _) => ToggleResetOnResume());
        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => ToggleAutostart());

        BuildIntervalMenu();
        BuildIdleMenu();

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
            _idleItem,
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

        _sitting.Start();
        RefreshTheme(force: true);
        Refresh();

        _timer = new System.Windows.Forms.Timer { Interval = TickIntervalMilliseconds };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private bool IsPaused => !_sitting.IsRunning;

    private TimeSpan Elapsed => _sitting.Elapsed;

    private TimeSpan Target => TimeSpan.FromMinutes(Math.Max(_settings.TargetMinutes, AppSettings.MinTargetMinutes));

    private double Progress => Math.Clamp(Elapsed.TotalSeconds / Target.TotalSeconds, 0d, 1d);

    /// <summary>One pass: apply the idle rule, advance the blink, repaint and relabel.</summary>
    private void Refresh()
    {
        ApplyIdleRule();
        RefreshTheme(force: false);

        double progress = Progress;
        bool finished = progress >= 1d;

        if (finished && !IsPaused && !_targetAnnounced)
        {
            _targetAnnounced = true;
            if (_settings.ShowNotification)
            {
                AnnounceTarget();
            }
        }

        bool shouldBlink = finished && !IsPaused && _settings.BlinkWhenFinished;
        _blinkVisible = shouldBlink ? !_blinkVisible : true;

        UpdateIcon(progress, finished);
        UpdateTooltip(finished);
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

    private void UpdateIcon(double progress, bool finished)
    {
        int size = TrayIconSize();

        // Quantising the progress keeps the redraw from running when nothing would change on screen.
        int progressStep = (int)Math.Round(progress * 500d);
        string signature = $"{size}|{progressStep}|{finished}|{IsPaused}|{_lightBackground}|{_blinkVisible}";
        if (signature == _renderedSignature && _currentIcon is not null)
        {
            return;
        }

        _renderedSignature = signature;

        var state = new HourglassState(
            Progress: progress,
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
        _notifyIcon.ShowBalloonTip(
            10_000,
            "Timerlight",
            $"Вы за компьютером уже {FormatDuration(Elapsed)}. Пора размяться — щёлкните по значку, чтобы начать отсчёт заново.",
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

    private void ApplyInterval(int minutes)
    {
        _settings.TargetMinutes = Math.Clamp(minutes, AppSettings.MinTargetMinutes, AppSettings.MaxTargetMinutes);
        _settings.Save();
        _targetAnnounced = false;
        _renderedSignature = string.Empty;
        Refresh();
    }

    private void ApplyIdleReset(int minutes)
    {
        _settings.IdleResetMinutes = Math.Max(0, minutes);
        _settings.Save();
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
        foreach (ToolStripItem item in _intervalItem.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is int minutes)
            {
                menuItem.Checked = minutes == _settings.TargetMinutes;
            }
        }

        _idleItem.Text = _settings.IdleResetMinutes > 0
            ? $"Сброс при бездействии: {FormatDuration(TimeSpan.FromMinutes(_settings.IdleResetMinutes))}"
            : "Сброс при бездействии: выключен";
        foreach (ToolStripItem item in _idleItem.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is int minutes)
            {
                menuItem.Checked = minutes == _settings.IdleResetMinutes;
            }
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"""
             Timerlight {typeof(TrayApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}

             Песочные часы в трее показывают, сколько вы сидите за компьютером.
             Цвет песка идёт от зелёного к красному, а в конце интервала значок мигает.

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
