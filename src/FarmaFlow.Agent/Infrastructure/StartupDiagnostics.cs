namespace FarmaFlow.Agent.Infrastructure;

public static class StartupDiagnostics
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FarmaFlow",
        "Agent",
        "logs");

    public static void ReportFatal(Exception exception, string context)
    {
        Write(exception, context);

        try
        {
            MessageBox.Show(
                $"O FarmaFlow Agent não conseguiu iniciar.\n\n{exception.Message}\n\nConsulte o log em:\n{LogDirectory}",
                "FarmaFlow Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Logging must never cause a second startup failure.
        }
    }

    public static void Write(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"agent-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not terminate the agent.
        }
    }
}
