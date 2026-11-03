using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MuteOnBlur;

sealed record AppEntry(string AppId, string? ExePath, bool Audible);

sealed record Snapshot(IReadOnlyList<AppEntry> Managed, IReadOnlyList<AppEntry> Playing)
{
    public static readonly Snapshot Empty = new([], []);
}

/// <summary>
/// Owns every Core Audio object on one dedicated MTA thread (session notifications are only
/// delivered to MTA clients). Public methods may be called from any thread; they queue work.
///
/// Rule: a session is muted iff its app is managed and is not the foreground app. We only ever
/// unmute sessions we muted ourselves, so mutes the user set in the mixer are left alone.
/// </summary>
sealed class MuteController : IDisposable
{
    const int PollIntervalMs = 2000;
    const float AudiblePeak = 0.001f;

    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;
    readonly SessionNotifier _notifier;
    readonly Dictionary<string, IAudioSessionManager2> _managers = new();
    readonly HashSet<string> _seen = new();
    readonly HashSet<string> _managed;
    readonly HashSet<string> _mutedByUs;
    IMMDeviceEnumerator? _enumerator;
    string? _foreground;
    bool _stopping;
    int _disposed;

    public MuteController(string? initialForeground)
    {
        _foreground = initialForeground;
        _managed = new(Storage.LoadManaged(), StringComparer.OrdinalIgnoreCase);
        _mutedByUs = new(Storage.LoadMutedByUs());
        _notifier = new SessionNotifier(() => Post(() => Reconcile(onlyNew: true)));
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void SetForeground(string appId) => Post(() =>
    {
        Log.Debug($"Foreground: {appId}");
        _foreground = appId;
        Reconcile(onlyNew: false);
    });

    public void SetManaged(string appId, bool managed) => Post(() =>
    {
        if (!(managed ? _managed.Add(appId) : _managed.Remove(appId))) return;
        Log.Info($"{(managed ? "Managing" : "Unmanaging")} {appId}");
        Storage.SaveManaged(_managed);
        Reconcile(onlyNew: false);
    });

    public Snapshot GetSnapshot(int timeoutMs = 1000)
    {
        if (Volatile.Read(ref _disposed) != 0) return Snapshot.Empty;
        var result = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { result.SetResult(BuildSnapshot()); }
            catch (Exception ex) { result.SetException(ex); }
        });
        return result.Task.Wait(timeoutMs) ? result.Task.Result : throw new TimeoutException("Audio thread did not respond");
    }

    /// <summary>Unmutes everything we muted and stops the audio thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Post(Shutdown);
        _queue.CompleteAdding();
        if (!_thread.Join(3000)) Log.Error("Audio thread did not stop in time");
    }

    void Post(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { } // Shutting down.
    }

    void Run()
    {
        Execute(() =>
        {
            RefreshDevices();
            var snapshot = BuildSnapshot();
            Log.Info($"Foreground: {_foreground ?? "(none)"}; managed: [{string.Join(", ", snapshot.Managed.Select(a => a.AppId))}]; " +
                     $"playing: [{string.Join(", ", snapshot.Playing.Select(a => a.AppId + (a.Audible ? " (audible)" : "")))}]");
            Reconcile(onlyNew: false);
        });

        long nextPoll = Environment.TickCount64 + PollIntervalMs;
        while (!_stopping)
        {
            int wait = (int)Math.Max(0, nextPoll - Environment.TickCount64);
            if (_queue.TryTake(out var action, wait)) Execute(action);

            // Safety net: picks up added devices and any session whose creation notification we missed.
            if (!_stopping && Environment.TickCount64 >= nextPoll)
            {
                Execute(() =>
                {
                    RefreshDevices();
                    Reconcile(onlyNew: true);
                });
                nextPoll = Environment.TickCount64 + PollIntervalMs;
            }
        }
    }

    static void Execute(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("Audio thread error", ex); }
    }

    /// <summary>Keeps one session manager, with our notification registered, per active output device.</summary>
    void RefreshDevices()
    {
        _enumerator ??= CoreAudio.CreateDeviceEnumerator();
        _enumerator.EnumAudioEndpoints(EDataFlow.eRender, CoreAudio.DEVICE_STATE_ACTIVE, out var devices);
        var current = new HashSet<string>();
        try
        {
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out var device);
                try
                {
                    device.GetId(out string id);
                    current.Add(id);
                    if (_managers.ContainsKey(id)) continue;

                    var iid = CoreAudio.IID_IAudioSessionManager2;
                    device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object instance);
                    var manager = (IAudioSessionManager2)instance;
                    // Notifications only start flowing after the session list has been enumerated once.
                    manager.GetSessionEnumerator(out var sessions);
                    CoreAudio.Release(sessions);
                    manager.RegisterSessionNotification(_notifier);
                    _managers[id] = manager;
                    Log.Info($"Watching output device {id}");
                }
                catch (COMException ex)
                {
                    Log.Error("Could not open output device", ex);
                }
                finally
                {
                    CoreAudio.Release(device);
                }
            }
        }
        finally
        {
            CoreAudio.Release(devices);
        }

        foreach (var id in _managers.Keys.Where(id => !current.Contains(id)).ToList())
        {
            DropDevice(id);
            Log.Info($"Output device gone: {id}");
        }
    }

    void DropDevice(string id)
    {
        var manager = _managers[id];
        _managers.Remove(id);
        try { manager.UnregisterSessionNotification(_notifier); }
        catch (COMException) { }
        CoreAudio.Release(manager);
    }

    List<AudioSession> EnumerateSessions()
    {
        var result = new List<AudioSession>();
        List<string>? broken = null;
        foreach (var (id, manager) in _managers)
        {
            IAudioSessionEnumerator? sessions = null;
            try
            {
                manager.GetSessionEnumerator(out sessions);
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out object com);
                    if (AudioSession.TryCreate(com) is { } session) result.Add(session);
                }
            }
            catch (COMException ex)
            {
                // Device invalidated or audio service restarted; reopen it on the next poll.
                Log.Error($"Could not enumerate sessions on {id}", ex);
                (broken ??= []).Add(id);
            }
            finally
            {
                CoreAudio.Release(sessions);
            }
        }
        broken?.ForEach(DropDevice);
        return result;
    }

    /// <summary>Applies the mute rule to all sessions, or only to sessions not seen before.</summary>
    void Reconcile(bool onlyNew)
    {
        var sessions = EnumerateSessions();
        try
        {
            var unmuted = new List<string>();
            bool changed = false;
            foreach (var session in sessions)
            {
                bool isNew = _seen.Add(session.InstanceId);
                if (onlyNew && !isNew) continue;
                try
                {
                    bool shouldMute = _managed.Contains(session.AppId) && session.AppId != _foreground;
                    bool muted = session.IsMuted;
                    if (shouldMute && !muted)
                    {
                        session.SetMute(true);
                        changed |= _mutedByUs.Add(session.SessionId);
                        Log.Debug($"Muted {session.AppId} (pid {session.Pid})");
                    }
                    else if (!shouldMute && muted && _mutedByUs.Contains(session.SessionId))
                    {
                        session.SetMute(false);
                        // Removed after the loop: other sessions may share this identifier.
                        unmuted.Add(session.SessionId);
                        Log.Debug($"Unmuted {session.AppId} (pid {session.Pid})");
                    }
                }
                catch (COMException ex)
                {
                    Log.Error($"Could not update {session.AppId} (pid {session.Pid})", ex);
                }
            }
            foreach (var id in unmuted) changed |= _mutedByUs.Remove(id);
            _seen.IntersectWith(sessions.Select(s => s.InstanceId));
            if (changed) Storage.SaveMutedByUs(_mutedByUs);
        }
        finally
        {
            sessions.ForEach(s => s.Dispose());
        }
    }

    Snapshot BuildSnapshot()
    {
        var sessions = EnumerateSessions();
        try
        {
            var apps = sessions
                .GroupBy(s => s.AppId)
                .Select(g => new
                {
                    AppId = g.Key,
                    g.First().ExePath,
                    Active = g.Any(s => s.State == AudioSessionState.Active),
                    Audible = g.Any(s => s.State == AudioSessionState.Active && s.Peak > AudiblePeak),
                })
                .ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);

            var managed = _managed
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(id => apps.TryGetValue(id, out var a) ? new AppEntry(id, a.ExePath, a.Audible) : new AppEntry(id, null, false))
                .ToList();
            var playing = apps.Values
                .Where(a => /* a.Active && */ !_managed.Contains(a.AppId))
                .OrderByDescending(a => a.Audible)
                .ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
                .Select(a => new AppEntry(a.AppId, a.ExePath, a.Audible))
                .ToList();
            return new Snapshot(managed, playing);
        }
        finally
        {
            sessions.ForEach(s => s.Dispose());
        }
    }

    void Shutdown()
    {
        try
        {
            var sessions = EnumerateSessions();
            try
            {
                foreach (var session in sessions.Where(s => _mutedByUs.Contains(s.SessionId)))
                {
                    try
                    {
                        if (!session.IsMuted) continue;
                        session.SetMute(false);
                        Log.Debug($"Unmuted {session.AppId} (pid {session.Pid}) on exit");
                    }
                    catch (COMException) { }
                }
                // Identifiers of apps that are not running stay recorded, so the next run can undo
                // the mute Windows remembers for them.
                _mutedByUs.ExceptWith(sessions.Select(s => s.SessionId));
                Storage.SaveMutedByUs(_mutedByUs);
            }
            finally
            {
                sessions.ForEach(s => s.Dispose());
            }

            foreach (var id in _managers.Keys.ToList()) DropDevice(id);
            CoreAudio.Release(_enumerator);
        }
        finally
        {
            _stopping = true;
        }
    }
}
