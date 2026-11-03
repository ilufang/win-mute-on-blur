using System.Runtime.InteropServices;

namespace MuteOnBlur;

/// <summary>
/// One audio session (what the volume mixer shows per app) resolved to its executable.
/// Wraps a COM object; dispose it on the thread that enumerated it.
/// </summary>
sealed class AudioSession : IDisposable
{
    // Tags our own mute changes so they can be told apart from other apps' changes.
    static Guid EventContext = new("3c5e8d6a-5f0b-4f6e-9d0e-7a3b2c1d4e5f");
    static readonly int OwnPid = Environment.ProcessId;

    readonly object _com;

    /// <summary>Unique per running session; used to spot sessions not seen before.</summary>
    public string InstanceId { get; }
    /// <summary>Stable across app restarts (device + executable); Windows remembers mute by this.</summary>
    public string SessionId { get; }
    public string AppId { get; }
    public string ExePath { get; }
    public uint Pid { get; }
    public AudioSessionState State { get; }

    AudioSession(object com, string instanceId, string sessionId, string exePath, uint pid, AudioSessionState state)
    {
        _com = com;
        InstanceId = instanceId;
        SessionId = sessionId;
        ExePath = exePath;
        AppId = Win32.AppIdFromPath(exePath);
        Pid = pid;
        State = state;
    }

    /// <summary>Wraps a session from IAudioSessionEnumerator, or releases it and returns null if it is not an app session.</summary>
    public static AudioSession? TryCreate(object com)
    {
        try
        {
            var control = (IAudioSessionControl2)com;
            control.GetState(out var state);
            if (state != AudioSessionState.Expired
                && control.IsSystemSoundsSession() != 0
                && control.GetProcessId(out uint pid) == 0
                && pid != 0 && pid != OwnPid
                && Win32.GetProcessPath(pid) is { } path)
            {
                control.GetSessionInstanceIdentifier(out string instanceId);
                control.GetSessionIdentifier(out string sessionId);
                return new AudioSession(com, instanceId, sessionId, path, pid, state);
            }
        }
        catch (COMException)
        {
            // Session vanished mid-enumeration.
        }
        CoreAudio.Release(com);
        return null;
    }

    public bool IsMuted
    {
        get
        {
            ((ISimpleAudioVolume)_com).GetMute(out bool muted);
            return muted;
        }
    }

    public void SetMute(bool mute) => ((ISimpleAudioVolume)_com).SetMute(mute, ref EventContext);

    public float Peak
    {
        get
        {
            try
            {
                ((IAudioMeterInformation)_com).GetPeakValue(out float peak);
                return peak;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    public void Dispose() => CoreAudio.Release(_com);
}
