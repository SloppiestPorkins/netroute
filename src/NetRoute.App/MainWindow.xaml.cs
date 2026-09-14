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

        SourceInitialized += (_, _) => UseDarkTitleBar();
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

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
