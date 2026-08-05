using BattTray.Devices;
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

    AppSettings _settings = SettingsStore.Load();
    SettingsForm? _settingsForm;
    Icon? _currentIcon;

    /// <summary>Theme the loaded icon was chosen for; null until the first load.</summary>
    bool? _iconMatchesLightTheme;

    public TrayApplicationContext()
    {
        // No image column: the rows carry a percentage in their text, so a glyph beside
        // each one would only add width and repeat what the words already say.
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += OnMenuOpening;

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = "BattTray",
        };
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
            _settingsForm.Activate();
            return;
        }

        using var form = new SettingsForm(_settings);
        _settingsForm = form;

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
