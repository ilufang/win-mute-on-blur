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

    const int PollIntervalMs = 500;
    // How soon after an event to check. Events come in bursts (a foreground event plus minimize-end, or a
    // stale desktop event right after a real switch); one check once the burst is over covers them all.
    const int EventDelayMs = 30;

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    readonly Win32.WinEventDelegate _callback; // Kept referenced so the GC does not collect it while hooked.
    readonly IntPtr[] _hooks;
    readonly System.Windows.Forms.Timer _timer;
    ForegroundWindow _lastCapture;

    /// <summary>
    /// The app that owns the foreground window, ignoring shell surfaces and this tool. Null when no app
    /// has focus: the foreground window is minimized (Win-D can leave a minimized window foreground).
    /// </summary>
    public string? Current { get; private set; }

    public event Action<string?>? Changed;

    /// <summary>The foreground window as captured, before any ignore rules apply.</summary>
    readonly record struct ForegroundWindow(IntPtr Hwnd, string ClassName, uint Pid, string? AppId)
    {
        public override string ToString() => $"hwnd 0x{Hwnd:X}, class \"{ClassName}\", pid {Pid}, app {AppId ?? "(unknown)"}";
    }

    public FocusWatcher()
    {
        // Events only bring the next check forward; the check itself reads the foreground window. Event
        // timing cannot be trusted: a foreground event may arrive before GetForegroundWindow changes, after
        // a later switch (stale desktop events when leaving Win-D), or not at all (Win-D on a window that
        // cannot be minimized). The poll is what keeps Current right.
        _callback = (_, eventType, hwnd, _, _, _, _) => OnEvent(eventType, hwnd);
        _hooks = [Hook(Win32.EVENT_SYSTEM_FOREGROUND), Hook(Win32.EVENT_SYSTEM_MINIMIZESTART), Hook(Win32.EVENT_SYSTEM_MINIMIZEEND)];

        var (window, app, reason) = Evaluate();
        Log.Debug($"Initial focus: {window} -> {Describe(app, reason)}");
        Current = app;

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    IntPtr Hook(uint eventType) =>
        Win32.SetWinEventHook(eventType, eventType, IntPtr.Zero, _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

    void OnEvent(uint eventType, IntPtr hwnd)
    {
        try
        {
            var source = eventType switch
            {
                Win32.EVENT_SYSTEM_FOREGROUND => "foreground",
                Win32.EVENT_SYSTEM_MINIMIZESTART => "minimize-start",
                _ => "minimize-end",
            };
            Log.Debug($"Event {source}: {Capture(hwnd)}");

            // Wake the poll early. A timer already woken is left alone, so a burst of events collapses
            // into one check and a steady stream of them cannot keep postponing it.
            if (_timer.Interval != EventDelayMs)
            {
                _timer.Stop();
                _timer.Interval = EventDelayMs;
                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Foreground tracking error", ex);
        }
    }

    void OnTick()
    {
        try
        {
            bool afterEvent = _timer.Interval != PollIntervalMs;
            if (afterEvent)
            {
                _timer.Stop();
                _timer.Interval = PollIntervalMs;
                _timer.Start();
            }

            var (window, app, reason) = Evaluate();
            bool changed = reason == null && app != Current;
            // Checks after an event are always logged, so the outcome follows the event lines; quiet polls are not.
            if (changed || afterEvent)
                Log.Debug($"Check{(afterEvent ? "" : " (poll)")}: {window} -> {Describe(app, reason)}{(changed || reason != null ? "" : " (unchanged)")}");
            if (!changed) return;
            Current = app;
            Changed?.Invoke(app);
        }
        catch (Exception ex)
        {
            Log.Error("Foreground tracking error", ex);
        }
    }

    /// <summary>
    /// The foreground window and the app that counts as focused (null: none), or why the window does
    /// not change the focused app.
    /// </summary>
    (ForegroundWindow Window, string? App, string? IgnoreReason) Evaluate()
    {
        var window = CaptureForeground();
        // A minimized window can stay foreground; nothing of it is in front of the user then.
        if (window.Hwnd != IntPtr.Zero && Win32.IsIconic(window.Hwnd)) return (window, null, null);
        return IgnoreReason(window) is { } reason ? (window, null, reason) : (window, window.AppId, null);
    }

    static string Describe(string? app, string? reason) =>
        reason != null ? $"ignored: {reason}" : app ?? "(none: minimized)";

    /// <summary>Captures the foreground window, reusing the last capture while it is the same window (nearly every poll).</summary>
    ForegroundWindow CaptureForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        // A UWP capture holds the child app's pid, so it never matches and is redone; that also covers a new
        // frame whose app has not attached its child window yet.
        if (hwnd != IntPtr.Zero && hwnd == _lastCapture.Hwnd && pid == _lastCapture.Pid && _lastCapture.AppId != null
            && _lastCapture.AppId != "applicationframehost.exe")
            return _lastCapture;
        return _lastCapture = Capture(hwnd);
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
        _timer.Dispose();
        foreach (var hook in _hooks)
        {
            if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
        }
    }
}
