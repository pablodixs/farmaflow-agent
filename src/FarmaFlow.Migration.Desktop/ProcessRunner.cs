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
        bool captureOutput = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Executável não encontrado.", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach ((string key, string value) in environment) startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(executable)}.");
        Task<string> outputTask = captureOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        Task<string> errorTask = captureOutput
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        if (input is not null)
        {
            await using StreamWriter writer = process.StandardInput;
            foreach (string line in input) await writer.WriteLineAsync(line);
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}
