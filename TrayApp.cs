using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace MuteOnBlur;

/// <summary>
/// Tray icon plus the context menu: running managed apps at the top level, managed apps that are not
/// running under "Managed", and unmanaged playing apps under "Playing".
/// </summary>
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
    readonly Dictionary<string, (string? Description, Icon? Icon)> _appVisuals = new(StringComparer.OrdinalIgnoreCase);
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

    /// <param name="reopen">Text of the submenu to open again after the rebuild, the one an app was toggled in.</param>
    void Rebuild(string? reopen = null)
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
        DisposeItems(_menu.Items);

        AddHeader("Muting upon blur");
        var running = snapshot.Managed.Where(a => a.Running).ToList();
        foreach (var app in running) AddApp(_menu.Items, app, managed: true, submenu: null);
        if (running.Count == 0) AddHeader("    (none)");

        _menu.Items.Add(new ToolStripSeparator());
        var submenus = new[]
        {
            AddSubmenu("Managed", "Click to remove", "(Non-running managed apps will show here)",
                snapshot.Managed.Where(a => !a.Running), managed: true),
            AddSubmenu("Playing", "Click to manage", "(Unmanaged apps playing audio will show here)",
                snapshot.Playing, managed: false),
        };

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        _menu.ResumeLayout();

        submenus.FirstOrDefault(s => s.Text == reopen)?.ShowDropDown();
    }

    static void DisposeItems(ToolStripItemCollection items)
    {
        var old = items.Cast<ToolStripItem>().ToList();
        items.Clear();
        foreach (var item in old)
        {
            if (item is ToolStripDropDownItem { HasDropDownItems: true } parent) DisposeItems(parent.DropDownItems);
            item.Image?.Dispose();
            item.Dispose();
        }
    }

    void AddHeader(string text) => AddHeader(_menu.Items, text);

    static void AddHeader(ToolStripItemCollection items, string text) =>
        items.Add(new ToolStripMenuItem(text) { Enabled = false });

    /// <summary>A cascading item holding a header and the given apps, or the placeholder when there are none.</summary>
    ToolStripMenuItem AddSubmenu(string text, string desc, string placeholder, IEnumerable<AppEntry> apps, bool managed)
    {
        var submenu = new ToolStripMenuItem(text);
        AddHeader(submenu.DropDownItems, desc);
        foreach (var app in apps) AddApp(submenu.DropDownItems, app, managed, text);
        if (submenu.DropDownItems.Count == 1) AddHeader(submenu.DropDownItems, placeholder);
        _menu.Items.Add(submenu);
        return submenu;
    }

    void AddApp(ToolStripItemCollection items, AppEntry app, bool managed, string? submenu)
    {
        var (text, icon) = GetVisual(app);
        var item = new ToolStripMenuItem(text.Replace("&", "&&"))
        {
            Checked = managed,
            Image = icon?.ToBitmap(),
            ShortcutKeyDisplayString = app.Audible ? "♪" : null,
            ToolTipText = app.ExePath,
        };
        item.Click += (_, _) =>
        {
            _controller.SetManaged(app.AppId, !managed);
            // Rebuild after the click handler returns; the snapshot request queues behind the change.
            _menu.BeginInvoke(() => Rebuild(submenu));
        };
        items.Add(item);
    }

    /// <summary>"Name (exe)" and the exe's icon. The name is the mixer's, else the exe's description.</summary>
    (string Text, Icon? Icon) GetVisual(AppEntry app)
    {
        var (description, icon) = GetExeVisual(app);
        var name = app.Name ?? description;
        bool useful = !string.IsNullOrEmpty(name) && !name.Equals(app.AppId, StringComparison.OrdinalIgnoreCase);
        return (useful ? $"{name} ({app.AppId})" : app.AppId, icon);
    }

    /// <summary>The exe's description and icon, cached per app once its path has been seen.</summary>
    (string? Description, Icon? Icon) GetExeVisual(AppEntry app)
    {
        if (_appVisuals.TryGetValue(app.AppId, out var cached)) return cached;
        if (app.ExePath == null) return (null, null);

        string? description = null;
        Icon? icon = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(app.ExePath).FileDescription?.Trim();
            icon = Icon.ExtractAssociatedIcon(app.ExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return _appVisuals[app.AppId] = (description, icon);
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
