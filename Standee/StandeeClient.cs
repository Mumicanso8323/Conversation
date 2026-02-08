using System.Diagnostics;
using System.IO.Pipes;

namespace Conversation.Standee;

public sealed class StandeeClient {
    private readonly StandeeConfig _config;

    public StandeeClient(StandeeConfig config) {
        _config = config;
    }

    public async Task InitializeAsync(CancellationToken ct) {
        try {
            await EnsureConnectedAsync(ct);
            await SendAsync($"SET|{StandeeSprites.Default}", ct);
            await SendAsync("SHOW|", ct);
        }
        catch (Exception ex) {
            Console.WriteLine($"[standee] init skipped: {ex.Message}");
        }
    }

    public async Task SetSpriteAsync(string sprite, CancellationToken ct = default) {
        var normalized = StandeeSprites.Normalize(sprite);
        try {
            await EnsureConnectedAsync(ct);
            await SendAsync($"SET|{normalized}", ct);
        }
        catch (Exception ex) {
            Console.WriteLine($"[standee] set skipped: {ex.Message}");
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct) {
        try {
            await SendAsync("PING|", ct);
            return;
        }
        catch {
            TryStartViewer();
            await Task.Delay(600, ct);
        }
    }

    private async Task SendAsync(string message, CancellationToken ct) {
        using var client = new NamedPipeClientStream(".", _config.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(700, ct);
        await using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(message);
    }

    private void TryStartViewer() {
        try {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[] {
                Path.Combine(baseDir, "StandeeViewer.exe"),
                Path.Combine(baseDir, "StandeeViewer", "StandeeViewer.exe")
            };

            foreach (var path in candidates) {
                if (File.Exists(path)) {
                    Process.Start(new ProcessStartInfo {
                        FileName = path,
                        UseShellExecute = true
                    });
                    return;
                }
            }

            var projectPath = Path.Combine(baseDir, "StandeeViewer", "StandeeViewer.csproj");
            if (File.Exists(projectPath)) {
                Process.Start(new ProcessStartInfo {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{projectPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"[standee] viewer start failed: {ex.Message}");
        }
    }
}
