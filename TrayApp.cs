using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace MuteOnBlur;

/// <summary>Tray icon plus the context menu that lists managed and currently playing apps.</summary>
sealed class TrayApp : ApplicationContext
{
    // NotifyIcon only opens its menu on right-click; this internal method opens it the same way for left-click.
    static readonly MethodInfo? ShowContextMenu =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    readonly FocusWatcher _focus;
    readonly MuteController _controller;
    readonly ContextMenuStrip _menu;
    readonly NotifyIcon _tray;
    readonly Icon _icon;
    readonly Dictionary<string, (string Text, Icon? Icon)> _appVisuals = new(StringComparer.OrdinalIgnoreCase);
    bool _exiting;

    public TrayApp()
    {
        _focus = new FocusWatcher();
        _controller = new MuteController(_focus.Current);
        _focus.Changed += _controller.SetForeground;

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, e) =>
        {
            Rebuild();
            e.Cancel = false;
        };
        // Keep the menu open after toggling an app, so several can be changed in one go.
        _menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && !_exiting) e.Cancel = true;
        };

        _icon = CreateTrayIcon();
        _tray = new NotifyIcon { Icon = _icon, Text = "Mute on Blur", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowContextMenu?.Invoke(_tray, null);
        };

        SystemEvents.SessionEnded += OnSessionEnded;
    }

    void OnSessionEnded(object? sender, SessionEndedEventArgs e) => Exit();

    void Rebuild()
    {
        Snapshot snapshot;
        try
        {
            snapshot = _controller.GetSnapshot();
        }
        catch (Exception ex)
        {
            Log.Error("Could not list audio sessions", ex);
            snapshot = Snapshot.Empty;
        }

        _menu.SuspendLayout();
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in old)
        {
            item.Image?.Dispose();
            item.Dispose();
        }

        AddHeader("Muting upon blur");
        foreach (var app in snapshot.Managed) AddApp(app, managed: true);
        if (snapshot.Managed.Count == 0) AddHeader("    (none)");

        _menu.Items.Add(new ToolStripSeparator());
        AddHeader("Playing now");
        foreach (var app in snapshot.Playing) AddApp(app, managed: false);
        if (snapshot.Playing.Count == 0) AddHeader("    (none)");

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        _menu.ResumeLayout();
    }

    void AddHeader(string text) => _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });

    void AddApp(AppEntry app, bool managed)
    {
        var (text, icon) = GetVisual(app);
        var item = new ToolStripMenuItem(text.Replace("&", "&&"))
        {
            Checked = managed,
            Image = icon?.ToBitmap(),
            ShortcutKeyDisplayString = !managed && app.Audible ? "♪" : null,
            ToolTipText = app.ExePath,
        };
        item.Click += (_, _) =>
        {
            _controller.SetManaged(app.AppId, !managed);
            // Rebuild after the click handler returns; the snapshot request queues behind the change.
            _menu.BeginInvoke(Rebuild);
        };
        _menu.Items.Add(item);
    }

    /// <summary>"Description (exe)" and the exe's icon, cached per app once its path has been seen.</summary>
    (string Text, Icon? Icon) GetVisual(AppEntry app)
    {
        if (_appVisuals.TryGetValue(app.AppId, out var cached)) return cached;
        if (app.ExePath == null) return (app.AppId, null);

        string text = app.AppId;
        Icon? icon = null;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(app.ExePath).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && !description.Equals(app.AppId, StringComparison.OrdinalIgnoreCase))
                text = $"{description} ({app.AppId})";
            icon = Icon.ExtractAssociatedIcon(app.ExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return _appVisuals[app.AppId] = (text, icon);
    }

    /// <summary>The embedded app.ico, at the image closest to the tray's icon size.</summary>
    static Icon CreateTrayIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico")
            ?? throw new InvalidOperationException("app.ico is not embedded");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        SystemEvents.SessionEnded -= OnSessionEnded;
        _menu.Close();
        _tray.Visible = false;
        _focus.Dispose();
        _controller.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            foreach (var (_, icon) in _appVisuals.Values) icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
