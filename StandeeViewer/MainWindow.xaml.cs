using System.IO.Pipes;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace StandeeViewer;

public partial class MainWindow : Window {
    private readonly StandeeConfig _config;
    private readonly string _assetsDir;
    private readonly CancellationTokenSource _cts = new();

    public MainWindow() {
        InitializeComponent();

        var baseDir = AppContext.BaseDirectory;
        _config = StandeeConfig.LoadOrDefault(Path.Combine(baseDir, "standee_config.json"));
        _assetsDir = Path.Combine(baseDir, "assets", "standee");

        Topmost = _config.AlwaysOnTop;
        ConfigureWindowBounds();
        LoadSprite(StandeeSprites.Default);

        _ = Task.Run(() => RunPipeServerAsync(_cts.Token));
    }

    protected override void OnClosed(EventArgs e) {
        _cts.Cancel();
        base.OnClosed(e);
    }

    private void ConfigureWindowBounds() {
        var screens = Screen.AllScreens;
        var index = _config.MonitorIndex;
        if (index < 0 || index >= screens.Length) {
            index = 0;
        }

        var target = screens[index].Bounds;
        var width = _config.Window.Width ?? target.Width;
        var height = _config.Window.Height ?? target.Height;
        var scale = _config.Window.Scale ?? 1.0;

        Width = Math.Max(1, width * scale);
        Height = Math.Max(1, height * scale);
        Left = _config.Window.X ?? target.Left;
        Top = _config.Window.Y ?? target.Top;
    }

    private async Task RunPipeServerAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                using var server = new NamedPipeServerStream(_config.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line)) {
                    await Dispatcher.InvokeAsync(() => ProcessCommand(line));
                }
            }
            catch (OperationCanceledException) {
                return;
            }
            catch {
                await Task.Delay(200, ct);
            }
        }
    }

    private void ProcessCommand(string line) {
        var sep = line.IndexOf('|');
        var cmd = sep >= 0 ? line[..sep] : line;
        var payload = sep >= 0 ? line[(sep + 1)..] : string.Empty;

        switch (cmd) {
            case "SET":
                LoadSprite(StandeeSprites.Normalize(payload));
                break;
            case "SHOW":
                Show();
                break;
            case "HIDE":
                Hide();
                break;
            case "PING":
            default:
                break;
        }
    }

    private void LoadSprite(string fileName) {
        var safeName = StandeeSprites.Normalize(fileName);
        var path = Path.Combine(_assetsDir, safeName);
        if (!File.Exists(path)) {
            path = Path.Combine(_assetsDir, StandeeSprites.Default);
        }

        if (!File.Exists(path)) {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        StandeeImage.Source = bitmap;
    }
}
