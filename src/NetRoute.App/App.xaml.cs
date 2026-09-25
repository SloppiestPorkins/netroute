using System.IO;
using System.Windows;
using NetRoute.Ipc;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NetRoute.App;

/// <summary>
/// Startup: one instance, a tray icon that outlives the window, and an exception log.
/// The app only displays and edits; the service does the enforcing (§42), so exiting
/// the app never turns protection off.
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private bool _hintShown;
    private readonly List<(string Title, string Message)> _held = [];
    private Forms.Timer? _heldTimer;

    internal static bool ExitRequested { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        var args = e.Args;
        var screenshot = Value(args, "--screenshot");

        if (screenshot is null)
        {
            _instance = new Mutex(true, @"Local\NetRoute.App", out var first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NetRoute.App.Show");
            if (!first)
            {
                _showSignal.Set();   // bring the running copy forward instead
                ExitRequested = true;
                Shutdown();
                return;
            }
            new Thread(() =>
            {
                while (_showSignal.WaitOne())
                {
                    Dispatcher.Invoke(ShowWindow);
                }
            }) { IsBackground = true }.Start();
        }

        INetRouteClient client = args.Contains("--demo")
            ? new DemoNetRouteClient(firstRun: args.Contains("--first-run"))
            : new NamedPipeNetRouteClient();

        _vm = new MainViewModel(client);
        _window = new MainWindow(_vm, screenshot, Value(args, "--view"));
        MainWindow = _window;

        if (screenshot is null)
        {
            CreateTray();
            _vm.Notify += Notify;
        }

        if (!args.Contains("--minimized"))
        {
            _window.Show();
        }
    }

    public void OnWindowHidden()
    {
        if (_hintShown || _tray is null)
        {
            return;
        }
        _hintShown = true;
        _tray.ShowBalloonTip(4000, "NetRoute is still running",
            "Your apps stay on the network you chose. Open NetRoute from the tray icon.", Forms.ToolTipIcon.Info);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open NetRoute", null, (_, _) => ShowWindow()) { Font = new Drawing.Font(Forms.Control.DefaultFont, Drawing.FontStyle.Bold) };
        var gaming = new Forms.ToolStripMenuItem("Gaming") { Enabled = false };
        var downloads = new Forms.ToolStripMenuItem("Downloads") { Enabled = false };
        // Pausing asks how long, so protection can't be left off by accident (a pause is saved and
        // survives restarts). Resume is its own item, shown only while paused.
        var pause = new Forms.ToolStripMenuItem("Pause protection");
        foreach (var (label, minutes) in new (string, int?)[] { ("For 15 minutes", 15), ("For 1 hour", 60), ("For 3 hours", 180), ("Until I turn it back on", null) })
        {
            pause.DropDownItems.Add(label, null, (_, _) => _vm?.PauseForCommand.Execute(minutes));
        }
        var resume = new Forms.ToolStripMenuItem("Resume protection", null, (_, _) => _vm?.ResumeCommand.Execute(null));
        var emergency = new Forms.ToolStripMenuItem("Emergency Disable…", null, (_, _) =>
        {
            ShowWindow();
            _vm?.EmergencyDisableCommand.Execute(null);
        });
        var exit = new Forms.ToolStripMenuItem("Exit NetRoute app", null, (_, _) =>
        {
            ExitRequested = true;
            if (_tray is not null)
            {
                _tray.Visible = false;
            }
            Shutdown();
        });

        menu.Items.AddRange([open, new Forms.ToolStripSeparator(), gaming, downloads, new Forms.ToolStripSeparator(), pause, resume, emergency, new Forms.ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            var roles = _vm?.Roles;
            gaming.Text = "🎮 Gaming: " + (roles?.FirstOrDefault(r => r.Role == Core.Policy.RoleId.Gaming) is { } g ? $"{g.AdapterName} ({g.HealthText})" : "not set");
            downloads.Text = "⬇ Downloads: " + (roles?.FirstOrDefault(r => r.Role == Core.Policy.RoleId.Downloads) is { } d ? $"{d.AdapterName} ({d.HealthText})" : "not set");
            pause.Visible = _vm?.Paused != true;
            resume.Visible = _vm?.Paused == true;
            resume.Text = _vm?.OverallText is { } text && text.StartsWith("Paused until", StringComparison.Ordinal)
                ? $"Resume protection ({text.ToLowerInvariant()})" : "Resume protection";
        };

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "NetRoute",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        // Anything held during a game is said once the game lets go of the screen.
        _heldTimer = new Forms.Timer { Interval = 5000 };
        _heldTimer.Tick += (_, _) => ReleaseHeldNotifications();

        if (_vm is not null)
        {
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Paused))
                {
                    UpdateTrayIcon();
                }
            };
        }
    }

    /// <summary>
    /// Says something, unless the user is mid-game. A balloon during a full-screen game steals
    /// focus and can minimise it, which is the loudest complaint about every app of this kind, so
    /// anything that arrives then is held and said afterwards.
    /// </summary>
    private void Notify(string title, string message)
    {
        if (AcceptsNotifications())
        {
            _tray?.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
            return;
        }
        _held.Add((title, message));
        if (_held.Count > 5)
        {
            _held.RemoveAt(0);
        }
        _heldTimer?.Start();
    }

    /// <summary>False during a full-screen game, a presentation, or while Focus assist is on.</summary>
    private static bool AcceptsNotifications()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) != 0 || state == QunsAcceptsNotifications;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void ReleaseHeldNotifications()
    {
        if (_held.Count == 0 || !AcceptsNotifications())
        {
            return;
        }
        _heldTimer?.Stop();
        var held = _held.ToList();
        _held.Clear();
        if (held.Count == 1)
        {
            _tray?.ShowBalloonTip(5000, held[0].Title, held[0].Message, Forms.ToolTipIcon.Info);
            return;
        }
        _tray?.ShowBalloonTip(6000, $"{held.Count} things happened while you were playing",
            string.Join("  ·  ", held.Select(h => h.Title)), Forms.ToolTipIcon.Info);
    }

    private const int QunsAcceptsNotifications = 5;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Greys the icon and adds pause bars while paused, so a forgotten pause is visible at a glance.</summary>
    private void UpdateTrayIcon()
    {
        if (_tray is null)
        {
            return;
        }
        var paused = _vm?.Paused == true;
        var old = _tray.Icon;
        _tray.Icon = MakeTrayIcon(paused);
        _tray.Text = paused ? "NetRoute (paused)" : "NetRoute";
        if (old is not null)
        {
            DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A small rounded tile with two routes, drawn at runtime so there's no binary asset to ship.</summary>
    private static Drawing.Icon MakeTrayIcon(bool paused = false)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var tile = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1F, 0x23, 0x2C));
            g.FillEllipse(tile, 1, 1, 30, 30);
            var grey = Drawing.Color.FromArgb(0x7A, 0x80, 0x8C);
            using var green = new Drawing.Pen(paused ? grey : Drawing.Color.FromArgb(0x22, 0xC5, 0x5E), 3.2f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            using var blue = new Drawing.Pen(paused ? grey : Drawing.Color.FromArgb(0x4F, 0x8D, 0xF7), 3.2f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            g.DrawBezier(green, 8, 24, 12, 24, 14, 10, 24, 9);
            g.DrawBezier(blue, 8, 24, 16, 24, 18, 22, 24, 23);
            if (paused)
            {
                using var bars = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF5, 0xA5, 0x24));
                g.FillRectangle(bars, 19, 2, 4, 11);
                g.FillRectangle(bars, 25, 2, 4, 11);
            }
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;   // otherwise a ghost icon stays until the mouse passes over it
            _tray.Dispose();
        }
        base.OnExit(e);
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static void Log(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRoute");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "gui.log"), $"[{DateTimeOffset.Now:u}] {ex}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
