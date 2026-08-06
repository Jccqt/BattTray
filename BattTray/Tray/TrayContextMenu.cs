namespace BattTray.Tray;

/// <summary>
/// The tray menu, which differs from a plain <see cref="ContextMenuStrip"/> in one way:
/// it keeps itself off the taskbar.
/// </summary>
/// <remarks>
/// Opened from a tray icon there is no form behind it, so the drop-down is an unowned
/// top-level window — and Windows gives every unowned top-level window a taskbar button,
/// which is why one appeared for an app that has no window to switch to. The tool-window
/// style is how a window declares itself chrome rather than a destination.
/// </remarks>
internal sealed class TrayContextMenu : ContextMenuStrip
{
    const int WS_EX_TOOLWINDOW = 0x00000080;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TOOLWINDOW;
            return parameters;
        }
    }
}
