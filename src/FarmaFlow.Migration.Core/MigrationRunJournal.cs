using System.Text.Json;

namespace FarmaFlow.Migration.Core;

public sealed record MigrationRunState(
    string RunId,
    string Mode,
    string CurrentStep,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string> Results);

public sealed class MigrationRunJournal(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(MigrationRunState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<MigrationRunState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MigrationRunState>(stream, JsonOptions, cancellationToken);
    }
}

public sealed record OperationProgress(string Step, string Message, int? Percent = null);
