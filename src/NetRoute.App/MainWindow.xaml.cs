using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NetRoute.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly string? _screenshot;
    private readonly string? _screenshotView;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow(MainViewModel vm, string? screenshot = null, string? screenshotView = null)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _screenshot = screenshot;
        _screenshotView = screenshotView;

        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            RegisterHotkeys();
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _vm.Overlay is not null)
            {
                _vm.Overlay = null;
                e.Handled = true;
            }
        };
        // Poll quickly while visible, gently from the tray.
        IsVisibleChanged += (_, _) => _poll.Interval = TimeSpan.FromSeconds(IsVisible ? 2 : 10);
        _poll.Tick += async (_, _) => await _vm.RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshAsync();
        _poll.Start();
        if (_screenshot is not null)
        {
            await CaptureAsync(_screenshot);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.ExitRequested || _screenshot is not null)
        {
            return;
        }
        // Closing the window doesn't stop protection: the service does that job (§41, §42).
        e.Cancel = true;
        Hide();
        (Application.Current as App)?.OnWindowHidden();
    }

    /// <summary>Renders the window to a PNG and exits. Used to check the design without a person at the screen.</summary>
    private async Task CaptureAsync(string path)
    {
        switch (_screenshotView)
        {
            case "add":
                await _vm.OpenAddAppAsync();
                break;
            case "why" when _vm.Apps.Count > 0:
                _vm.WhyCommand.Execute(_vm.Apps[0]);
                break;
            case "change" when _vm.Apps.Count > 0:
                _vm.ChangeNetworkCommand.Execute(_vm.Apps[0]);
                break;
            case "live":
                await _vm.OpenLiveAsync();
                break;
            case "emergency":
                _vm.EmergencyDisableCommand.Execute(null);
                break;
            case "checkup":
                await _vm.OpenCheckupAsync();
                break;
            case "selftest":
                await _vm.OpenSelfTestAsync();
                break;
            case "history":
                await _vm.OpenHistoryAsync();
                break;
            case "pause":
                _vm.TogglePauseAllCommand.Execute(null);
                break;
            case "suggest":
                await _vm.OpenSuggestDownloadsAsync();
                break;
        }

        await Task.Delay(600);
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var file = File.Create(path))
        {
            encoder.Save(file);
        }
        App.ExitRequested = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Ctrl+Alt+N brings NetRoute up, Ctrl+Alt+P pauses it for 15 minutes. Mid-game, reaching the
    /// tray means leaving the game; a hotkey doesn't. If another program already owns a combination
    /// Windows simply refuses it, which is not worth bothering the user about.
    /// </summary>
    private void RegisterHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        RegisterHotKey(handle, HotkeyShow, ModControl | ModAlt, 'N');
        RegisterHotKey(handle, HotkeyPause, ModControl | ModAlt, 'P');
        HwndSource.FromHwnd(handle)?.AddHook(HotkeyHook);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }
        switch ((int)wParam)
        {
            case HotkeyShow:
                if (IsVisible && WindowState != WindowState.Minimized)
                {
                    Hide();
                }
                else
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                }
                handled = true;
                break;
            case HotkeyPause:
                _vm.PauseForCommand.Execute(15);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private const int WmHotkey = 0x0312;
    private const int HotkeyShow = 1;
    private const int HotkeyPause = 2;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
