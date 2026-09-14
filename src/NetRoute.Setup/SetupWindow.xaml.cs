using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetRoute.Setup;

public partial class SetupWindow : Window
{
    private readonly SetupModel _model;
    private readonly string _screenshot;
    private readonly string _page;

    public SetupWindow(SetupModel model, string screenshot, string page)
    {
        InitializeComponent();
        DataContext = _model = model;
        _screenshot = screenshot;
        _page = page;

        SourceInitialized += (_, __) =>
        {
            var on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));   // dark title bar
        };
        Closing += OnClosing;
        if (screenshot != null)
        {
            Loaded += async (_, __) => await CaptureAsync();
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // Stopping part way would leave NetRoute half installed.
        if (_model.Page == "Progress" && _screenshot == null)
        {
            e.Cancel = true;
        }
    }

    /// <summary>Renders the window to a PNG and closes. For checking the design without installing anything.</summary>
    private async Task CaptureAsync()
    {
        if (!string.IsNullOrEmpty(_page))
        {
            _model.Preview(_page);
        }
        await Task.Delay(400);
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_screenshot)));
        using (var file = File.Create(_screenshot))
        {
            encoder.Save(file);
        }
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
