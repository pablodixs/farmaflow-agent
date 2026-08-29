namespace FarmaFlow.Server.Host;

public sealed class ServerHostOptions
{
    public int PublicPort { get; init; } = 8443;
    public string BackendUrl { get; init; } = "http://127.0.0.1:8180";
    public string WebUrl { get; init; } = "http://127.0.0.1:3100";
    public string RuntimeDirectory { get; init; } = "runtime";
    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FarmaFlow", "Server");
    public string? ExternalBackupDirectory { get; init; }
    public string BackupTime { get; init; } = "02:00";
    public string Version { get; init; } = "development";

    public string ResolveRuntimePath(params string[] parts)
    {
        string root = Path.IsPathRooted(RuntimeDirectory)
            ? RuntimeDirectory
            : Path.Combine(AppContext.BaseDirectory, RuntimeDirectory);
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }
}
