using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>
/// Reports the two gestures that mean "I am done with that popup": pressing a mouse
/// button somewhere else on the desktop, and bringing another window to the foreground.
/// </summary>
/// <remarks>
/// A popup that was shown without taking activation is told about neither. The framework
/// dismisses drop-downs off activation the window never received, and the shell only
/// light-dismisses its own flyouts off input it never sees, so both hooks are global:
/// the interesting input lands in other processes. They are installed only while a popup
/// is on screen. Neither swallows what it observes — the click still reaches whatever sits
/// under it, which is what lets the rest of the desktop react as it normally would.
/// </remarks>
internal sealed class OutsideInteractionHook : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int HC_ACTION = 0;

    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_MBUTTONDOWN = 0x0207;
    const int WM_XBUTTONDOWN = 0x020B;

    /// <summary>
    /// How long after a click on the popup a foreground change is read as that click's own
    /// doing rather than as the user turning to another window.
    /// </summary>
    /// <remarks>
    /// Clicking a popup that belongs to a tray icon can move the foreground on its own: the
    /// shell takes the click as its cue to put its hidden-icons flyout away and hands the
    /// foreground to the taskbar, which arrives while the click is still being turned into
    /// an item activation. Dismissing on that cancels the click, and the item the user
    /// picked never runs. Generous next to the millisecond or so it takes to arrive, and
    /// bounded so that a genuine switch a moment later is still caught.
    /// </remarks>
    const long ClickGraceMilliseconds = 750;

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int OBJID_WINDOW = 0;

    readonly Func<Point, bool> _isInsidePopup;
    readonly Action _onOutsideInteraction;

    // Held in fields for as long as the hooks are installed: these are handed to unmanaged
    // code, which does not keep the collector from taking them.
    readonly HookProc _mouseCallback;
    readonly WinEventProc _foregroundCallback;

    nint _mouseHook;
    nint _foregroundHook;

    /// <summary>The foreground window when watching began; a change away from it dismisses.</summary>
    nint _initialForeground;

    /// <summary>When the popup was last clicked, as a tick count; see the grace period.</summary>
    long _clickedAt;

    /// <summary>Marshals back to the thread that started watching, off the hook callbacks.</summary>
    SynchronizationContext? _context;

    /// <summary>Reported once per watch, so a drag or a double click does not fire twice.</summary>
    bool _triggered;

    public OutsideInteractionHook(Func<Point, bool> isInsidePopup, Action onOutsideInteraction)
    {
        _isInsidePopup = isInsidePopup;
        _onOutsideInteraction = onOutsideInteraction;
        _mouseCallback = OnMouseInput;
        _foregroundCallback = OnForegroundChanged;
    }

    /// <summary>Begins watching. Must be called from the thread that owns the popup.</summary>
    public void Start()
    {
        if (_mouseHook != 0 || _foregroundHook != 0)
            return;

        _triggered = false;
        _clickedAt = 0;
        _context = SynchronizationContext.Current;
        _initialForeground = GetForegroundWindow();

        // A low-level mouse hook has to name a module even though the callback lives in
        // this process; the executable's own handle is what the documented pattern uses.
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseCallback, GetModuleHandleW(null), 0);

        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundCallback,
            idProcess: 0, idThread: 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Stop()
    {
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    public void Dispose() => Stop();

    nint OnMouseInput(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION && IsButtonDown((int)wParam))
        {
            var input = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
            if (_isInsidePopup(input.Point))
                _clickedAt = Environment.TickCount64;
            else
                Trigger();
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    void OnForegroundChanged(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        // Only whole windows, and only a window other than the one the popup was opened
        // over: the shell reasserts the foreground of its own flyouts, and treating that
        // as a switch would close the popup the instant it appeared.
        if (idObject != OBJID_WINDOW || hwnd == 0 || hwnd == _initialForeground)
            return;

        if (Environment.TickCount64 - _clickedAt < ClickGraceMilliseconds)
            return;

        Trigger();
    }

    /// <summary>
    /// Hands the report to the popup's own thread. A hook callback is expected back
    /// promptly — the system stops calling one that dawdles — and closing a popup from
    /// inside the callback that is still unwinding invites re-entrancy for no gain.
    /// </summary>
    void Trigger()
    {
        if (_triggered)
            return;

        _triggered = true;

        if (_context is { } context)
            context.Post(_ => _onOutsideInteraction(), null);
        else
            _onOutsideInteraction();
    }

    static bool IsButtonDown(int message) =>
        message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    delegate nint HookProc(int nCode, nint wParam, nint lParam);

    delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    /// <summary>MSLLHOOKSTRUCT; only the screen position is of interest here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MouseLowLevelHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hmod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string? lpModuleName);
}
