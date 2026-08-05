using BattTray.Settings;

namespace BattTray.Tray;

/// <summary>
/// The only window the app has: a small modal dialog opened from the tray menu.
/// </summary>
/// <remarks>
/// Built in code rather than with the designer so the whole layout is readable in one
/// place, and sized by <see cref="Control.AutoSize"/> rather than fixed coordinates so it
/// stays correct at any DPI and font scale. It is constructed on demand and disposed on
/// close, which keeps the resident footprint the same as before this existed.
/// </remarks>
internal sealed class SettingsForm : Form
{
    readonly CheckBox _startWithWindows;
    readonly CheckBox _notify;
    readonly ComboBox _threshold;
    readonly CheckBox _hideDisconnected;
    readonly ComboBox _refreshInterval;

    /// <summary>The settings as edited, valid once the dialog returns <see cref="DialogResult.OK"/>.</summary>
    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Result = settings;

        Text = "BattTray Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        // No title-bar icon: the tray glyph comes in one colour per theme, and the one that
        // contrasts with the taskbar is the wrong one against a title bar.
        ShowIcon = false;

        _startWithWindows = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            // Read from the registry, not from the settings file: another startup manager
            // may have removed it since the app launched.
            Checked = AutoStart.IsEnabled(),
        };

        _threshold = BuildChoice(
            AppSettings.Thresholds, settings.LowBatteryThreshold, static value => $"{value}%");
        _threshold.Enabled = settings.LowBatteryNotifications;

        _notify = new CheckBox
        {
            Text = "Warn me when a device runs low",
            AutoSize = true,
            Checked = settings.LowBatteryNotifications,
        };

        // A threshold with warnings switched off is a control that does nothing.
        _notify.CheckedChanged += (_, _) => _threshold.Enabled = _notify.Checked;

        _hideDisconnected = new CheckBox
        {
            Text = "Hide disconnected devices",
            AutoSize = true,
            Checked = settings.HideDisconnected,
        };

        _refreshInterval = BuildChoice(
            AppSettings.RefreshIntervals, settings.RefreshIntervalSeconds, DescribeInterval);

        Controls.Add(BuildLayout());
    }

    Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddHeading(layout, "Startup", first: true);
        AddSpanned(layout, _startWithWindows);

        AddHeading(layout, "Notifications");
        AddSpanned(layout, _notify);
        AddPair(layout, "Alert at or below", _threshold);

        AddHeading(layout, "Display");
        AddSpanned(layout, _hideDisconnected);
        AddPair(layout, "Refresh every", _refreshInterval);

        AddSpanned(layout, BuildButtons());
        return layout;
    }

    Control BuildButtons()
    {
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Apply();

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        return buttons;
    }

    void Apply()
    {
        Result = new AppSettings
        {
            LowBatteryNotifications = _notify.Checked,
            LowBatteryThreshold = SelectedValue(_threshold),
            HideDisconnected = _hideDisconnected.Checked,
            RefreshIntervalSeconds = SelectedValue(_refreshInterval),
        };

        // Applied here rather than on click so cancelling leaves the registry untouched.
        if (_startWithWindows.Checked != AutoStart.IsEnabled()
            && !AutoStart.SetEnabled(_startWithWindows.Checked))
        {
            MessageBox.Show(
                this,
                "Windows would not let BattTray change its startup entry.\n\n"
                + "If you are running from the build output rather than an installed copy, "
                + "start the app from BattTray.exe and try again.",
                "BattTray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    static ComboBox BuildChoice(int[] options, int selected, Func<int, string> describe)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        foreach (int option in options)
            combo.Items.Add(new Choice(option, describe(option)));

        combo.SelectedIndex = Math.Max(0, Array.IndexOf(options, selected));
        return combo;
    }

    static int SelectedValue(ComboBox combo) => ((Choice)combo.SelectedItem!).Value;

    static string DescribeInterval(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        60 => "1 minute",
        _ => $"{seconds / 60} minutes",
    };

    static void AddHeading(TableLayoutPanel layout, string text, bool first = false)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, first ? 0 : 12, 0, 4),
        };

        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
    }

    static void AddSpanned(TableLayoutPanel layout, Control control)
    {
        layout.Controls.Add(control);
        layout.SetColumnSpan(control, 2);
    }

    static void AddPair(TableLayoutPanel layout, string text, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            // Nudged down so the text sits on the combo box's baseline.
            Margin = new Padding(3, 6, 8, 3),
        });

        layout.Controls.Add(control);
    }

    /// <summary>A combo entry that shows a label but yields the number behind it.</summary>
    sealed record Choice(int Value, string Text)
    {
        public override string ToString() => Text;
    }
}
