using System.Diagnostics;

namespace FarmaFlow.Migration.Desktop;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    internal static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IEnumerable<string>? input = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Executável não encontrado.", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach ((string key, string value) in environment) startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(executable)}.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            await using StreamWriter writer = process.StandardInput;
            foreach (string line in input) await writer.WriteLineAsync(line);
        }
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}
