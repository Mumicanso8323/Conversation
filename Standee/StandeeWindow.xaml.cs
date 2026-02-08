namespace Conversation.Standee;

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public partial class StandeeWindow : Window {
    private readonly StandeeConfig _config;

    public StandeeWindow(StandeeConfig config) {
        InitializeComponent();
        _config = config;
        ShowInTaskbar = config.ShowInTaskbar;
        Topmost = config.Topmost;
        if (config.DebugVisibleBackground) {
            Background = new SolidColorBrush(Color.FromArgb(10, 255, 0, 0));
        }

        SourceInitialized += OnSourceInitialized;
        LoadSprite(StandeeSprites.Default);
    }

    public void LoadSprite(string fileName) {
        var candidate = StandeeSprites.IsAllowed(fileName) ? fileName : StandeeSprites.Default;
        var spritePath = Path.Combine(AppContext.BaseDirectory, "assets", "standee", candidate);

        if (!File.Exists(spritePath)) {
            spritePath = Path.Combine(AppContext.BaseDirectory, "assets", "standee", StandeeSprites.Default);
            if (!File.Exists(spritePath)) {
                SpriteImage.Source = null;
                return;
            }
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(spritePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        SpriteImage.Source = image;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) {
        ApplyWindowPlacement();
        ApplyClickThrough();
    }

    private void ApplyWindowPlacement() {
        var screens = Screen.AllScreens;
        var idx = Math.Clamp(_config.MonitorIndex, 0, Math.Max(0, screens.Length - 1));
        var screen = screens.Length > 0 ? screens[idx] : Screen.PrimaryScreen;
        if (screen is null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = _config.Scale <= 0 ? 1.0 : _config.Scale;

        var pxW = (_config.Window.Width ?? 512) * scale;
        var pxH = (_config.Window.Height ?? 768) * scale;

        Width = pxW / dpi.DpiScaleX;
        Height = pxH / dpi.DpiScaleY;

        var xPx = _config.Window.X ?? screen.Bounds.Left;
        var yPx = _config.Window.Y ?? screen.Bounds.Top;

        Left = xPx / dpi.DpiScaleX;
        Top = yPx / dpi.DpiScaleY;

        var workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        var workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        var workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        var workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;

        if (Width > workRight - workLeft) Width = workRight - workLeft;
        if (Height > workBottom - workTop) Height = workBottom - workTop;

        Left = Math.Clamp(Left, workLeft, Math.Max(workLeft, workRight - Width));
        Top = Math.Clamp(Top, workTop, Math.Max(workTop, workBottom - Height));
    }

    private void ApplyClickThrough() {
        if (!_config.ClickThrough) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExLayered | WsExTransparent | WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
