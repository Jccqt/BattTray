using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>
/// Reports that a PnP device has arrived or gone away, so a view of what is attached can be
/// dropped and rebuilt at the moment it stops being true rather than at the next poll.
/// </summary>
/// <remarks>
/// <para>
/// This uses cfgmgr32's <c>CM_Register_Notification</c> rather than <c>WM_DEVICECHANGE</c>.
/// The window message needs a window to be delivered to, and this app deliberately owns no
/// resident window — one created purely to receive broadcasts would be the only window it
/// has — whereas the notification API takes a callback and nothing else.
/// </para>
/// <para>
/// Both registrations are as broad as their filter allows, which is the opposite of what
/// the per-interface-class form exists for, and it is deliberate. What these events do is
/// authorise a cache: a filter narrow enough to miss one arrival would leave that cache
/// wrong until the device went away again, which is a worse failure than the one it saves.
/// The extra events cost a coalesced refresh, and the caller coalesces anyway because a
/// single device arriving fires several of them regardless of how the filter is drawn.
/// </para>
/// </remarks>
internal sealed class DeviceChangeWatcher : IDisposable
{
    const uint CR_SUCCESS = 0;
    const uint ERROR_SUCCESS = 0;

    // CM_NOTIFY_FILTER_TYPE_*: the device-handle form is not used here, as it wants a handle
    // to an already-opened device and this watcher is about devices that do not exist yet.
    const uint FilterTypeDeviceInterface = 0;
    const uint FilterTypeDeviceInstance = 2;

    // CM_NOTIFY_FILTER_FLAG_*: what makes each filter cover everything rather than the one
    // interface class or instance id its union arm would otherwise name.
    const uint FlagAllInterfaceClasses = 0x00000001;
    const uint FlagAllDeviceInstances = 0x00000002;

    // CM_NOTIFY_ACTION_*.
    const uint ActionDeviceInterfaceArrival = 0;
    const uint ActionDeviceInterfaceRemoval = 1;
    const uint ActionDeviceRemoveComplete = 5;
    const uint ActionDeviceInstanceEnumerated = 7;
    const uint ActionDeviceInstanceStarted = 8;
    const uint ActionDeviceInstanceRemoved = 9;

    readonly Action _onDeviceChanged;

    /// <summary>Where <see cref="_onDeviceChanged"/> has to run; captured in TryStart.</summary>
    readonly SynchronizationContext _uiThread;

    /// <summary>
    /// Held in a field for as long as the registrations stand: this is handed to unmanaged
    /// code, which does not keep the collector from taking it.
    /// </summary>
    readonly NotifyCallback _callback;

    readonly List<nint> _registrations = [];

    DeviceChangeWatcher(Action onDeviceChanged, SynchronizationContext uiThread)
    {
        _onDeviceChanged = onDeviceChanged;
        _uiThread = uiThread;
        _callback = OnNotification;
    }

    /// <summary>
    /// Begins watching, or returns null when Windows would not register the notifications.
    /// </summary>
    /// <remarks>
    /// Call from the UI thread, after the first control exists: the callbacks are served by
    /// pool threads, so the handler has to be marshalled back, and the synchronization
    /// context that does the marshalling is installed by the first control the thread
    /// creates. Null is a supported answer rather than a fault — the caller is expected to
    /// go on polling, which covers the same ground more slowly.
    /// </remarks>
    public static DeviceChangeWatcher? TryStart(Action onDeviceChanged)
    {
        var uiThread = SynchronizationContext.Current;

        Debug.Assert(uiThread is not null, "No synchronization context: changes would land on a pool thread.");

        if (uiThread is null)
            return null;

        var watcher = new DeviceChangeWatcher(onDeviceChanged, uiThread);
        if (watcher.Register())
            return watcher;

        // Half a registration would claim coverage this does not have, so whichever filter
        // did take is taken back down and the caller is told nothing is watching.
        watcher.Dispose();
        return null;
    }

    /// <summary>
    /// Both halves of "a device appeared or disappeared". They are genuinely separate: a
    /// provider that enumerates device nodes is answered by the instance filter, one that
    /// enumerates interface paths by the interface filter, and neither implies the other.
    /// </summary>
    bool Register() =>
        Register(FilterTypeDeviceInterface, FlagAllInterfaceClasses)
        && Register(FilterTypeDeviceInstance, FlagAllDeviceInstances);

    bool Register(uint filterType, uint flags)
    {
        var filter = default(NotifyFilter);
        filter.Size = NotifyFilter.FilterSize;
        filter.FilterType = filterType;
        filter.Flags = flags;

        try
        {
            if (CM_Register_Notification(ref filter, 0, _callback, out nint registration) != CR_SUCCESS)
                return false;

            _registrations.Add(registration);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // CM_Register_Notification arrived in Windows 10 1709. Anything older is outside
            // what this app documents support for, but a missing export should still leave it
            // polling rather than refusing to start.
            Debug.WriteLine($"Device change notifications unavailable: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Runs on a threadpool thread, and is expected back promptly: the PnP manager serialises
    /// these per registration, so time spent here is time other notifications wait.
    /// </summary>
    uint OnNotification(nint notify, nint context, uint action, nint eventData, uint eventDataSize)
    {
        try
        {
            // The event payload names the device that changed. It is not read: every consumer
            // here re-enumerates rather than patching one entry, so which device it was makes
            // no difference to what happens next.
            if (IsDeviceChange(action))
                _uiThread.Post(_ => _onDeviceChanged(), null);
        }
        catch (Exception ex)
        {
            // An exception escaping a callback made from unmanaged code takes the process with
            // it, which is a far worse answer to a device change than no answer. The one
            // failure expected here is a post that lost a race with shutdown.
            Debug.WriteLine($"Device change dropped: {ex}");
        }

        return ERROR_SUCCESS;
    }

    /// <summary>
    /// Whether an action means the set of attached devices is now different. The query-remove
    /// pair is excluded because it is Windows asking permission rather than reporting a
    /// change, and the custom event because it is a driver talking about a device that was
    /// already there.
    /// </summary>
    static bool IsDeviceChange(uint action) =>
        action is ActionDeviceInterfaceArrival or ActionDeviceInterfaceRemoval
            or ActionDeviceRemoveComplete or ActionDeviceInstanceEnumerated
            or ActionDeviceInstanceStarted or ActionDeviceInstanceRemoved;

    public void Dispose()
    {
        // CM_Unregister_Notification waits for a callback that is already running to return,
        // so once this loop is through, nothing further can be posted to the UI thread. That
        // wait is also why the callback only ever posts: unregistering from inside one would
        // be waiting on itself. Idempotent, since the list is emptied as it goes.
        foreach (nint registration in _registrations)
            CM_Unregister_Notification(registration);

        _registrations.Clear();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint NotifyCallback(nint notify, nint context, uint action, nint eventData, uint eventDataSize);

    /// <summary>
    /// CM_NOTIFY_FILTER. Only the header is declared: the union that follows it holds an
    /// interface class GUID, a device handle or an instance id, and the flag-based filters
    /// used here name none of those. Its bytes still have to be there and zeroed, which is
    /// what the explicit size does, because the call validates <see cref="Size"/> against
    /// the structure it expects and fails outright on anything else.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = (int)FilterSize)]
    struct NotifyFilter
    {
        /// <summary>Four DWORDs, then a union whose largest arm is a MAX_DEVICE_ID_LEN string.</summary>
        public const uint FilterSize = 16 + (200 * 2);

        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public uint Flags;

        [FieldOffset(8)]
        public uint FilterType;

        [FieldOffset(12)]
        public uint Reserved;
    }

    // Exported without a W suffix: the strings it takes are inside the filter structure.
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Register_Notification(
        ref NotifyFilter pFilter, nint pContext, NotifyCallback pCallback, out nint pNotifyContext);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Unregister_Notification(nint notifyContext);
}
