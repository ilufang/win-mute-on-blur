namespace MuteOnBlur;

/// <summary>
/// Tracks which app owns the foreground window. Must live on the UI thread: the
/// out-of-context WinEvent hooks deliver their callbacks through its message loop.
/// </summary>
sealed class FocusWatcher : IDisposable
{
    // Shell surfaces that briefly take the foreground (taskbar, Alt-Tab, ...).
    // Focusing them is not switching apps, so the previous app keeps its sound.
    // The desktop (Progman, WorkerW) is not listed: showing it (Win-D) counts as leaving the app.
    static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
    };

    static readonly HashSet<string> IgnoredApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // "startmenuexperiencehost.exe",
        // "searchapp.exe",
        "searchui.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "lockapp.exe",
    };

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    readonly Win32.WinEventDelegate _callback; // Kept referenced so the GC does not collect it while hooked.
    readonly IntPtr[] _hooks;

    /// <summary>The app that last held the foreground, ignoring shell surfaces and this tool.</summary>
    public string? Current { get; private set; }

    public event Action<string>? Changed;

    /// <summary>The foreground window as captured, before any ignore rules apply.</summary>
    readonly record struct ForegroundWindow(IntPtr Hwnd, string ClassName, uint Pid, string? AppId)
    {
        public override string ToString() => $"hwnd 0x{Hwnd:X}, class \"{ClassName}\", pid {Pid}, app {AppId ?? "(unknown)"}";
    }

    public FocusWatcher()
    {
        _callback = (_, eventType, hwnd, _, _, _, _) => Update(eventType, hwnd);
        // Restoring a minimized window does not always raise a foreground event, hence the second hook.
        _hooks = [Hook(Win32.EVENT_SYSTEM_FOREGROUND), Hook(Win32.EVENT_SYSTEM_MINIMIZEEND)];

        var window = Capture(Win32.GetForegroundWindow());
        var reason = IgnoreReason(window);
        Log.Debug($"Initial focus: {window}{(reason != null ? $" -> ignored: {reason}" : "")}");
        Current = reason == null ? window.AppId : null;
    }

    IntPtr Hook(uint eventType) =>
        Win32.SetWinEventHook(eventType, eventType, IntPtr.Zero, _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

    void Update(uint eventType, IntPtr eventHwnd)
    {
        try
        {
            // A foreground event names the new foreground window; GetForegroundWindow can still return
            // the old one at this point (seen with Win-D). A restored window is not necessarily active,
            // so minimize-end asks for the current foreground instead.
            bool isForeground = eventType == Win32.EVENT_SYSTEM_FOREGROUND;
            var hwnd = isForeground && eventHwnd != IntPtr.Zero ? eventHwnd : Win32.GetForegroundWindow();
            var window = Capture(hwnd);
            var current = Win32.GetForegroundWindow();
            var mismatch = current != hwnd ? $" (GetForegroundWindow 0x{current:X})" : "";
            Log.Debug($"Focus {(isForeground ? "foreground" : "minimize-end")}{mismatch}: {window}");

            if (IgnoreReason(window) is { } reason)
            {
                Log.Debug($"  ignored: {reason}");
                return;
            }
            var app = window.AppId!;
            if (app == Current) return;
            Current = app;
            Changed?.Invoke(app);
        }
        catch (Exception ex)
        {
            Log.Error("Foreground tracking error", ex);
        }
    }

    static ForegroundWindow Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new(hwnd, "", 0, null);

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        var app = pid != 0 && Win32.GetProcessPath(pid) is { } path ? Win32.AppIdFromPath(path) : null;

        // Store (UWP) apps are framed by ApplicationFrameHost; the real app owns a child window.
        if (app == "applicationframehost.exe" && FindChildProcess(hwnd, pid) is { } childPid
            && Win32.GetProcessPath(childPid) is { } childPath)
        {
            pid = childPid;
            app = Win32.AppIdFromPath(childPath);
        }

        return new(hwnd, Win32.GetClassName(hwnd), pid, app);
    }

    /// <summary>Why focusing this window does not count as switching apps, or null if it does.</summary>
    static string? IgnoreReason(ForegroundWindow window) =>
        window.Hwnd == IntPtr.Zero ? "no foreground window"
        : IgnoredClasses.Contains(window.ClassName) ? "shell window class"
        : window.Pid == OwnPid ? "own window"
        : window.AppId == null ? "process not accessible"
        : IgnoredApps.Contains(window.AppId) ? "ignored app"
        : null;

    static uint? FindChildProcess(IntPtr hwnd, uint hostPid)
    {
        uint? found = null;
        Win32.EnumChildWindows(hwnd, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == hostPid) return true;
            found = childPid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
        }
    }
}
