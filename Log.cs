namespace MuteOnBlur;

static class Log
{
    private enum Severity {
        DEBUG,
        INFO,
        WARNING,
        ERROR
    }
    
    const long MaxSize = 1024 * 1024;
    static readonly string FilePath = Path.Combine(Storage.Dir, "log.txt");
    static readonly Lock Gate = new();
    
    /// <summary>Starts a fresh log once the old one gets large.</summary>
    public static void Start()
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxSize) File.Delete(FilePath);
        }
        catch (IOException) { }
        Info("Started");
    }

    public static void Debug(string message) => Write(Severity.DEBUG, message);
    public static void Info(string message) => Write(Severity.INFO, message);
    public static void Warning(string message) => Write(Severity.WARNING, message);
    public static void Error(string message, Exception? ex = null) => Write(Severity.ERROR, ex == null ? message : $"{message}: {ex}");

    static void Write(Severity level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        
#if !DEBUG
        if (level <= Severity.DEBUG) {
            return;
        }
#endif
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}
