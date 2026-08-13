using BattTray.Devices;
using BattTray.Diagnostics;
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

    /// <summary>
    /// How long a burst of device-change notifications is left to settle before rescanning.
    /// One headset connecting starts several profile nodes and publishes several interfaces,
    /// and every one of them is reported separately; without this, one device arriving would
    /// buy a dozen scans. Short enough that the delay is not the part anyone notices — a
    /// device's battery property often takes seconds longer than this to appear, which is the
    /// poll's job rather than something a longer wait here could fix.
    /// </summary>
    const int DeviceChangeSettleMilliseconds = 400;

    readonly PeripheralMonitor _monitor = new(
        new BluetoothPeripheralProvider(),
        new XInputGamepadProvider());
    readonly NotifyIcon _notifyIcon;
    readonly LowBatteryNotifier _notifier;
    readonly ContextMenuStrip _menu;
    readonly System.Windows.Forms.Timer _timer;

    /// <summary>Collects a burst of device changes into one scan; see the settle interval.</summary>
    readonly System.Windows.Forms.Timer _deviceChangeSettle;

    /// <summary>Reports arrivals and removals, or null when Windows would not register them.</summary>
    readonly DeviceChangeWatcher? _deviceChanges;

    /// <summary>Supplies the dismissal the menu cannot hear about itself; see OnTrayMouseUp.</summary>
    readonly OutsideInteractionHook _outsideInteraction;

    AppSettings _settings = SettingsStore.Load();
    SettingsForm? _settingsForm;
    Icon? _currentIcon;

    /// <summary>Theme the loaded icon was chosen for; null until the first load.</summary>
    bool? _iconMatchesLightTheme;

    /// <summary>The pending one-shot idle handler, held so it can be detached; see ShowSettingsWhenIdle.</summary>
    EventHandler? _startupIdle;

    /// <summary>Guards <see cref="Refresh"/> against being entered twice; see the remarks there.</summary>
    bool _refreshing;

    /// <param name="showSettings">
    /// Opens the settings dialog once the app is up, as the acknowledgement a launch the
    /// user performed by hand deserves.
    /// </param>
    public TrayApplicationContext(bool showSettings)
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

        _deviceChangeSettle = new System.Windows.Forms.Timer { Interval = DeviceChangeSettleMilliseconds };
        _deviceChangeSettle.Tick += OnDeviceChangeSettled;

        // After the menu, which is what installs the synchronization context the watcher
        // marshals its threadpool callbacks through, and after the timer those callbacks
        // start — which is all they touch, so one arriving from here on is harmless even
        // though this object is not finished. Before the first scan, and deliberately: a
        // device arriving in the gap between scanning and registering would be missed by
        // both, and the cache that scan fills would stay wrong until the device left.
        //
        // A refusal is not worth reporting to anyone — it leaves the app exactly as it was
        // before any of this existed, polling — but it does have to be passed on, since it
        // is what decides whether a provider may hold an enumeration between polls.
        _deviceChanges = DeviceChangeWatcher.TryStart(OnDeviceChanged);
        _monitor.DeviceChangesAreWatched = _deviceChanges is not null;

        Refresh();

        // Populate up front: an empty ContextMenuStrip can refuse to open, which would
        // mean the Opening handler never gets a chance to fill it.
        RebuildMenu();

        if (showSettings)
            ShowSettingsWhenIdle();
    }

    /// <summary>
    /// Answers a second launch of the exe, which has no UI of its own and exits at once.
    /// </summary>
    /// <remarks>
    /// Arrives on this thread through the synchronization context, so it can land in the
    /// middle of a menu or a dialog the user already has open. Both are handled: the menu
    /// is dismissed, because a dialog rising through an open drop-down looks like a fault,
    /// and an open dialog is raised rather than duplicated by <see cref="ShowSettings"/>.
    /// </remarks>
    public void ShowSettingsOnRequest()
    {
        _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        ShowSettings();
    }

    /// <summary>Opens the settings dialog as soon as the message loop is running.</summary>
    /// <remarks>
    /// Not called straight from the constructor: <see cref="Form.ShowDialog()"/> pumps
    /// messages itself, so the dialog would go up while this object was still half-built and
    /// before <see cref="Application.Run(ApplicationContext)"/> had been reached — leaving
    /// the tray icon unresponsive behind it. Idle is the first lull after the loop starts,
    /// which is the earliest moment the app is genuinely running.
    /// </remarks>
    void ShowSettingsWhenIdle()
    {
        _startupIdle = (_, _) =>
        {
            // Once only: idle fires again every time the app runs out of messages.
            Application.Idle -= _startupIdle;
            _startupIdle = null;
            ShowSettings();
        };

        Application.Idle += _startupIdle;
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

    /// <summary>
    /// A device has arrived or gone away. Already marshalled onto this thread by the watcher,
    /// so nothing here is off-thread.
    /// </summary>
    void OnDeviceChanged()
    {
        // The rest of this burst needs no timer of its own. Deliberately not a restart: an
        // arrival that kept reporting itself for longer than the settle interval would push
        // a restarting timer back indefinitely and the scan would never happen.
        if (_deviceChangeSettle.Enabled)
            return;

        _deviceChangeSettle.Start();
    }

    void OnDeviceChangeSettled(object? sender, EventArgs e)
    {
        // A Forms timer repeats, and there is nothing left to wait for.
        _deviceChangeSettle.Stop();

        // What the providers enumerated last time is no longer what is attached, which is
        // the one thing a poll cannot work out for itself.
        _monitor.InvalidateDeviceCache();
        Refresh();

        // The menu is not rebuilt here. A closed one is rebuilt as it opens, from a scan
        // newer than this one, and an open one is left alone on purpose: RebuildMenu
        // disposes every row it replaces, including the one under the user's cursor.
    }

    /// <summary>Rescans devices, updates the tooltip and alerts on low batteries.</summary>
    /// <remarks>
    /// Reached from the poll, the settle timer, the menu opening, and the user asking. All
    /// four are on this thread, but an open menu and the settings dialog pump messages of
    /// their own, so a timer tick can land part-way through a scan already in progress.
    /// The second one is dropped rather than queued: it would only read the same tree again,
    /// and whichever timer woke it will come round again shortly.
    /// </remarks>
    void Refresh()
    {
        if (_refreshing)
            return;

        _refreshing = true;

        try
        {
            _monitor.Refresh();
            ApplyThemeIcon();
            _notifyIcon.Text = BuildTooltip();

            // Given the full list, not the filtered one: hiding disconnected devices is a
            // display preference and must not silence a device that is genuinely low.
            _notifier.Evaluate(_monitor.Peripherals, _settings);
        }
        finally
        {
            _refreshing = false;
        }
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

        // Two questions, and they used to be one: what is here, and what will talk. A device
        // can be connected and silent — an XInput slot answering BATTERY_TYPE_WIRED, a
        // headset that publishes no battery node — and folding that into "none reporting"
        // told the user nothing was there while a controller sat plainly connected.
        var live = devices.Where(d => d.IsConnected).ToList();
        var reporting = live.Where(d => d.BatteryPercent is not null).ToList();
        var lowest = _monitor.LowestConnected;

        string text = (live.Count, reporting.Count) switch
        {
            // Everything on show is a leftover reading. Worth saying once here, because it
            // is the one thing every row in the menu below has in common.
            (0, _) => $"BattTray — {devices.Count} device(s), none connected",

            // Present and silent. Naming it beats counting: the complaint this answers is
            // "my controller is right there", and the answer is that it is seen and will
            // not say. The menu row alongside puts it the same way.
            (1, 0) => $"{live[0].Name}: no battery reported",
            (_, 0) => $"{live.Count} devices connected, none reporting a level",

            (_, 1) => $"{reporting[0].Name}: {reporting[0].BatteryText}",

            // The lowest device is named here, where it used to contribute a bare number.
            // A band forces the issue — "lowest low" is not a sentence — but the naming is
            // owed either way: an unattributed reading is the same ambiguity that keeps a
            // level off the tray icon. Over-long lines are truncated below as they always were.
            _ => $"{reporting.Count} devices — lowest {lowest?.Name}: {lowest?.BatteryText}",
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
                : "No devices found";

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
        _menu.Items.Add(new ToolStripMenuItem("Save diagnostics…", null, (_, _) => SaveDiagnostics()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(DescribeVersion()) { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));
    }

    /// <summary>
    /// The footer row: which build of the app this is.
    /// </summary>
    /// <remarks>
    /// The app has no window, no title bar and no About box, so until this row existed the only
    /// way to find out what was running was to go looking for the exe on disk and open its
    /// properties — which is a question a bug report asks first and a user could not answer
    /// without leaving the app entirely. It is the companion to the diagnostics header, and
    /// reads from the same place so the two cannot disagree.
    ///
    /// Named rather than a bare number, because a menu whose other rows are device names would
    /// otherwise leave "0.1.0" attached to nothing in particular.
    ///
    /// Disabled, like the device rows: it states a fact and there is nothing to click. That does
    /// cost the obvious gesture — clicking to copy the version — which is worth having only once
    /// there is somewhere for a click to go, and the row is the version's first home rather than
    /// its last.
    /// </remarks>
    internal static string DescribeVersion() => $"BattTray {AppVersion.Display}";

    /// <summary>
    /// Writes the diagnostics dump and opens Explorer on it.
    /// </summary>
    /// <remarks>
    /// The primary surface for the dump, and the reason it exists at all: the hardware this
    /// project needs evidence from belongs to people who downloaded an exe, and a menu row is
    /// the only instruction they can be given that does not begin "install the .NET SDK".
    ///
    /// Deliberately shares nothing with the running app but this click — no monitor, no
    /// settings — so that the file produced here is byte-for-byte the one the command-line
    /// form produces with nothing running. It blocks this thread for a second or two, most of
    /// it opening HID handles. That is visible only as a tray icon that ignores a hover, since
    /// the menu has already closed and there is no window to grey out.
    /// </remarks>
    static void SaveDiagnostics() => DiagnosticsFile.SaveAndReveal();

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

    /// <summary>
    /// One menu row: the name, the reading where there is one, and what the device is doing.
    /// </summary>
    /// <remarks>
    /// "No battery reported" rather than a blank, because a device that publishes no level is
    /// a thing worth stating: a connected XInput slot answering BATTERY_TYPE_WIRED and a
    /// connected headset with no battery node are both present, working, and silent about
    /// charge, and a row that simply omitted the clause would read as though the reading had
    /// been forgotten.
    ///
    /// The charging arm has never run. No provider sets <see cref="ChargeState.Charging"/> —
    /// see XInputGamepadProvider for why the one byte that looked as though it could does
    /// not — so it is kept as the rendering a charge source would land on rather than as
    /// something the menu has shown.
    ///
    /// The row states two facts and states each once: what the reading is, then what the
    /// device is doing. The age belongs to the first clause because it is the reading's age
    /// and not the link's — a device can be present and carrying a number from an earlier
    /// session, and that row has to read "87% (stale) · charging" rather than picking one of
    /// the two halves to believe. Which is why nothing here asks about connectedness to find
    /// out whether a number is current: see <see cref="Peripheral.IsStale"/>.
    /// </remarks>
    internal static string DescribeDevice(Peripheral device)
    {
        string battery = device.BatteryText switch
        {
            null => "no battery reported",
            { } text when device.IsStale => $"{text} (stale{FormatAge(device.BatteryUpdatedUtc)})",
            { } text => text,
        };

        string status = device switch
        {
            // Ahead of charging, which is a claim about a link that is no longer there.
            { IsConnected: false } => "disconnected",
            { ChargeState: ChargeState.Charging } => "charging",
            _ => "connected",
        };

        return $"{device.Name} — {battery} · {status}";
    }

    /// <summary>Renders how long ago a reading was taken, e.g. ", last seen 4d ago".</summary>
    internal static string FormatAge(DateTime? updatedUtc)
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
            // Static events: leaving these attached would keep the context alive for the
            // life of the process.
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_startupIdle is not null)
            {
                Application.Idle -= _startupIdle;
                _startupIdle = null;
            }
            // Global hooks: they outlive the object unless taken down explicitly.
            _outsideInteraction.Dispose();

            // Before the timer it feeds, and before anything it could reach: unregistering
            // waits out a callback that is already running, so once this returns nothing
            // further will be posted here. A post made just before it lands on a message
            // loop that has already ended, and is never dispatched.
            _deviceChanges?.Dispose();
            _deviceChangeSettle.Stop();
            _deviceChangeSettle.Dispose();

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
