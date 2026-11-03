namespace MuteOnBlur;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MuteOnBlur", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Mute on blur is already running (see the tray).", "Mute on blur");
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Start();
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);

        using var app = new TrayApp();
        Application.Run(app);
    }
}
