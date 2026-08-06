using BattTray.Devices;
using BattTray.Interop;
using BattTray.Settings;
using Microsoft.Win32;

namespace BattTray.Tray;

/// <summary>
/// The whole UI: a tray icon whose menu is rebuilt from a fresh scan each time it opens.
/// There is no resident window, which is what keeps the footprint small; the settings
/// dialog is built on demand and disposed on close.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>Shell tooltips are truncated past this length.</summary>
    const int MaxTooltipLength = 63;

    readonly PeripheralMonitor _monitor = new(new BluetoothPeripheralProvider());
    readonly NotifyIcon _notifyIcon;
    readonly LowBatteryNotifier _notifier;
    readonly ContextMenuStrip _menu;
    readonly System.Windows.Forms.Timer _timer;

    /// <summary>Supplies the dismissal the menu cannot hear about itself; see OnTrayMouseUp.</summary>
    readonly OutsideInteractionHook _outsideInteraction;

    AppSettings _settings = SettingsStore.Load();
    SettingsForm? _settingsForm;
    Icon? _currentIcon;

    /// <summary>Theme the loaded icon was chosen for; null until the first load.</summary>
    bool? _iconMatchesLightTheme;

    public TrayApplicationContext()
    {
        // No image column: the rows carry a percentage in their text, so a glyph beside
        // each one would only add width and repeat what the words already say.
        _menu = new TrayContextMenu { ShowImageMargin = false };

        // Bounds rather than the client area, so the border counts as part of the menu and
        // a click that lands on it is not read as a click on the desktop behind.
        _outsideInteraction = new OutsideInteractionHook(
            point => _menu.Visible && _menu.Bounds.Contains(point),
            () => _menu.Close(ToolStripDropDownCloseReason.AppClicked));

        _menu.Opening += OnMenuOpening;
        _menu.Opened += (_, _) => _outsideInteraction.Start();
        _menu.Closed += OnMenuClosed;

        // ContextMenuStrip is deliberately left unset: see OnTrayMouseUp.
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "BattTray",
        };
        _notifyIcon.MouseUp += OnTrayMouseUp;
        _notifyIcon.DoubleClick += (_, _) => Refresh();

        // The timeout only matters on shells old enough to honour it; modern Windows routes
        // balloon tips through the notification centre and picks its own duration.
        _notifier = new LowBatteryNotifier(
            (title, body) => _notifyIcon.ShowBalloonTip(10_000, title, body, ToolTipIcon.Warning));

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => Refresh();
        ApplyPollInterval();
        _timer.Start();

        // The poll would pick a theme change up eventually, but "eventually" is visible on
        // a taskbar that just inverted, so listen for the change instead. General is the
        // category the shell's ImmersiveColorSet broadcast arrives under.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Refresh();

        // Populate up front: an empty ContextMenuStrip can refuse to open, which would
        // mean the Opening handler never gets a chance to fill it.
        RebuildMenu();
    }

    /// <summary>
    /// Opens the menu on a right click, doing by hand what assigning
    /// <see cref="NotifyIcon.ContextMenuStrip"/> would otherwise do for us.
    /// </summary>
    /// <remarks>
    /// The built-in path calls SetForegroundWindow on the icon's hidden message window
    /// before showing the menu. When the icon sits behind the taskbar's chevron that is
    /// destructive: the shell's hidden-icons flyout is a light-dismiss popup, so losing
    /// activation makes it collapse the moment the pointer leaves it, and the menu is
    /// left hanging over a gap where the icons used to be. A drop-down shown this way is
    /// never activated, so foreground stays with the flyout and the flyout stays open.
    /// The cost is that dismissal has to be arranged by hand as well, since the framework
    /// closes a drop-down off activation this one never receives: see
    /// <see cref="_outsideInteraction"/>.
    /// </remarks>
    void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        var position = Cursor.Position;
        _menu.Show(position, DropDownDirectionFrom(position));
    }

    /// <summary>
    /// Picks the quadrant to unfold into so the menu grows away from the taskbar rather
    /// than across it, whichever edge the taskbar is docked to.
    /// </summary>
    static ToolStripDropDownDirection DropDownDirectionFrom(Point position)
    {
        var workingArea = Screen.FromPoint(position).WorkingArea;
        bool above = position.Y > workingArea.Top + (workingArea.Height / 2);
        bool left = position.X > workingArea.Left + (workingArea.Width / 2);

        return (above, left) switch
        {
            (true, true) => ToolStripDropDownDirection.AboveLeft,
            (true, false) => ToolStripDropDownDirection.AboveRight,
            (false, true) => ToolStripDropDownDirection.BelowLeft,
            (false, false) => ToolStripDropDownDirection.BelowRight,
        };
    }

    /// <remarks>
    /// The hidden-icons flyout is left to close itself. Hiding its window from here does
    /// dismiss it, but the shell goes on believing the flyout is up, so the next click on
    /// the chevron is spent putting that belief right and the user has to click twice.
    /// Since the click or the window switch that closes this menu is the same one the
    /// shell light-dismisses on, letting it do that keeps the two in step by itself.
    /// </remarks>
    void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e) =>
        _outsideInteraction.Stop();

    void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // A scan costs a couple of milliseconds, so it is cheaper to do it inline than to
        // show the user a stale list and swap it out underneath them. Items are rebuilt
        // only here, since there is no point maintaining rows nobody is looking at.
        Refresh();
        RebuildMenu();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            ApplyThemeIcon();
    }

    /// <summary>Rescans devices, updates the tooltip and alerts on low batteries.</summary>
    void Refresh()
    {
        _monitor.Refresh();
        ApplyThemeIcon();
        _notifyIcon.Text = BuildTooltip();

        // Given the full list, not the filtered one: hiding disconnected devices is a
        // display preference and must not silence a device that is genuinely low.
        _notifier.Evaluate(_monitor.Peripherals, _settings);
    }

    void ApplyPollInterval() =>
        _timer.Interval = (int)TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds).TotalMilliseconds;

    /// <summary>
    /// Swaps the icon only when the system theme has changed. The icon never reflects
    /// battery state, so there is nothing else that could make it need redrawing.
    /// </summary>
    void ApplyThemeIcon()
    {
        bool lightTheme = TrayIcons.IsSystemLightTheme();
        if (_iconMatchesLightTheme == lightTheme)
            return;

        var icon = TrayIcons.Load(lightTheme);
        _notifyIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
        _iconMatchesLightTheme = lightTheme;
    }

    string BuildTooltip()
    {
        var devices = _monitor.Peripherals;
        if (devices.Count == 0)
            return "BattTray — no devices";

        var connected = devices.Where(d => d.IsConnected && d.BatteryPercent is not null).ToList();
        string text = connected.Count switch
        {
            0 => $"BattTray — {devices.Count} device(s), none reporting",
            1 => $"{connected[0].Name}: {connected[0].BatteryPercent}%",
            _ => $"{connected.Count} devices — lowest {_monitor.LowestConnectedBattery}%",
        };

        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    void RebuildMenu()
    {
        // ToolStripItemCollection.Clear does not dispose what it removes, so the previous
        // rows have to be released by hand.
        var previous = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in previous)
            item.Dispose();

        var devices = _settings.HideDisconnected
            ? _monitor.Peripherals.Where(d => d.IsConnected).ToList()
            : _monitor.Peripherals;

        if (devices.Count == 0)
        {
            // Worded to distinguish "nothing is paired" from "you asked not to see the
            // devices that are paired but offline".
            string message = _settings.HideDisconnected && _monitor.Peripherals.Count > 0
                ? "No connected devices"
                : "No Bluetooth devices found";

            _menu.Items.Add(new ToolStripMenuItem(message) { Enabled = false });
        }
        else
        {
            foreach (var device in devices)
                _menu.Items.Add(CreateDeviceItem(device));
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Refresh", null, (_, _) => Refresh()));
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
    }

    void ShowSettings()
    {
        // A second copy of a dialog that writes the same file would let one overwrite the
        // other, so an open one is raised instead.
        if (_settingsForm is not null)
        {
            Surface(_settingsForm);
            return;
        }

        using var form = new SettingsForm(_settings);
        _settingsForm = form;
        form.Shown += (_, _) => Surface(form);

        try
        {
            if (form.ShowDialog() != DialogResult.OK)
                return;
        }
        finally
        {
            _settingsForm = null;
        }

        _settings = form.Result;
        SettingsStore.Save(_settings);

        ApplyPollInterval();
        Refresh();
        RebuildMenu();
    }

    /// <summary>Puts a window in front of the user and gives it the focus if it can.</summary>
    /// <remarks>
    /// The menu this is reached from never takes activation, so the app is not the window
    /// in front when a dialog goes up — and Windows may refuse to hand the foreground to an
    /// app that is not. A refused claim used to leave the dialog stranded behind whatever
    /// the user was looking at, with no taskbar button and no place in Alt+Tab to reach it
    /// by, so it read as a dialog that had failed to open. Raising the window is done first
    /// because the z-order is not rights-protected the way the foreground is: promoting it
    /// to topmost and straight back down leaves it above every ordinary window. The claim
    /// still follows, since it is what moves the keyboard focus when it is granted.
    /// </remarks>
    static void Surface(Form form)
    {
        form.TopMost = true;
        form.TopMost = false;
        form.Activate();
        ForegroundWindow.Claim(form.Handle);
    }

    static ToolStripItem CreateDeviceItem(Peripheral device) =>
        new ToolStripMenuItem(DescribeDevice(device))
        {
            // Nothing to click yet; the row is purely informational.
            Enabled = false,
        };

    static string DescribeDevice(Peripheral device)
    {
        string battery = device.BatteryPercent is { } percent ? $"{percent}%" : "battery unknown";

        string status = device switch
        {
            { IsConnected: false } => $"disconnected{FormatAge(device.BatteryUpdatedUtc)}",
            { ChargeState: ChargeState.Charging } => "charging",
            _ => "connected",
        };

        return $"{device.Name} — {battery} · {status}";
    }

    /// <summary>Renders how long ago a cached reading was taken, e.g. " · 4d ago".</summary>
    static string FormatAge(DateTime? updatedUtc)
    {
        if (updatedUtc is not { } timestamp)
            return string.Empty;

        var age = DateTime.UtcNow - timestamp;
        if (age < TimeSpan.Zero)
            return string.Empty;

        string rendered = age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours}h ago",
            _ => $"{(int)age.TotalDays}d ago",
        };

        return $", last seen {rendered}";
    }

    void ExitApplication()
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Static event: leaving this attached would keep the context alive for the life
            // of the process.
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            // Global hooks: they outlive the object unless taken down explicitly.
            _outsideInteraction.Dispose();
            _timer.Stop();
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _currentIcon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
