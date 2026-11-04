using System.Runtime.InteropServices;
using System.Text;

namespace MuteOnBlur;

static class Win32
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int PACKAGE_FULL_NAME_MAX_LENGTH = 127;

    public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    static extern int GetClassNameNative(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextNative(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", EntryPoint = "GetPackageFullName", CharSet = CharSet.Unicode)]
    static extern int GetPackageFullNameNative(IntPtr process, ref uint length, StringBuilder name);

    [DllImport("kernel32.dll", EntryPoint = "GetPackagePathByFullName", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullNameNative(string fullName, ref uint length, StringBuilder path);

    [DllImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameNative(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Full executable path of a process, or null if it is gone or inaccessible.</summary>
    public static string? GetProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Full name of the package (MSIX/Store app) a process belongs to, or null if it has none.</summary>
    public static string? GetPackageFullName(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(PACKAGE_FULL_NAME_MAX_LENGTH + 1);
            uint length = (uint)sb.Capacity;
            return GetPackageFullNameNative(handle, ref length, sb) == 0 ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Install folder of a package, or null if it is not installed.</summary>
    public static string? GetPackagePath(string fullName)
    {
        var sb = new StringBuilder(1024);
        uint length = (uint)sb.Capacity;
        return GetPackagePathByFullNameNative(fullName, ref length, sb) == 0 ? sb.ToString() : null;
    }

    /// <summary>Resolves an indirect string such as "@dll,-id" or "@{package?ms-resource://...}", or returns null.</summary>
    public static string? LoadIndirectString(string source)
    {
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(source, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>Title of the topmost visible, unowned, non-tool window of a process, or null if it has none.</summary>
    public static string? GetMainWindowTitle(uint pid)
    {
        string? title = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero
                || ((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return true;
            var sb = new StringBuilder(512);
            if (GetWindowTextNative(hwnd, sb, sb.Capacity) == 0 || string.IsNullOrWhiteSpace(sb.ToString())) return true;
            title = sb.ToString();
            return false;
        }, IntPtr.Zero);
        return title;
    }

    /// <summary>The app id used throughout: the lower-cased executable file name, e.g. "chrome.exe".</summary>
    public static string AppIdFromPath(string path) => Path.GetFileName(path).ToLowerInvariant();
}
