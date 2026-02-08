namespace Conversation;

using System.IO;

public static class AppPaths {
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;
    public static string LocalAppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conversation");

    public static string DataRoot { get; } = ResolveDataRoot();

    public static string LogsDir { get; } = Path.Combine(DataRoot, "logs");
    public static string LogFilePath { get; } = Path.Combine(LogsDir, "app.log");
    public static string SessionsDir { get; } = Path.Combine(DataRoot, "sessions");
    public static string NpcStatesDir { get; } = Path.Combine(DataRoot, "npc_states");
    public static string PsycheStatesDir { get; } = Path.Combine(DataRoot, "psyche_states");
    public static string SettingsDir { get; } = Path.Combine(DataRoot, "settings");

    public static string BaseSessionsDir { get; } = Path.Combine(BaseDirectory, "sessions");
    public static string BaseNpcStatesDir { get; } = Path.Combine(BaseDirectory, "npc_states");
    public static string BasePsycheStatesDir { get; } = Path.Combine(BaseDirectory, "psyche_states");
    public static string BaseNpcProfilesPath { get; } = Path.Combine(BaseDirectory, "npc_profiles.json");
    public static string BasePsycheProfilesPath { get; } = Path.Combine(BaseDirectory, "psyche_profiles.json");

    public static string DataNpcProfilesPath { get; } = Path.Combine(DataRoot, "npc_profiles.json");
    public static string DataPsycheProfilesPath { get; } = Path.Combine(DataRoot, "psyche_profiles.json");
    public static string SettingsFilePath { get; } = Path.Combine(SettingsDir, "settings.json");

    private static string ResolveDataRoot() {
        try {
            var baseHasLegacy = Directory.Exists(BaseSessionsDir)
                || Directory.Exists(BaseNpcStatesDir)
                || Directory.Exists(BasePsycheStatesDir)
                || File.Exists(BaseNpcProfilesPath)
                || File.Exists(BasePsycheProfilesPath);

            if (baseHasLegacy && IsWritable(BaseDirectory)) {
                EnsureSubDirs(BaseDirectory);
                return BaseDirectory;
            }
        }
        catch {
            // ignore
        }

        try {
            Directory.CreateDirectory(LocalAppDataRoot);
            EnsureSubDirs(LocalAppDataRoot);
            return LocalAppDataRoot;
        }
        catch {
            return BaseDirectory;
        }
    }

    private static bool IsWritable(string path) {
        try {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".writeprobe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch {
            return false;
        }
    }

    private static void EnsureSubDirs(string root) {
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "npc_states"));
        Directory.CreateDirectory(Path.Combine(root, "psyche_states"));
        Directory.CreateDirectory(Path.Combine(root, "settings"));
    }
}
