using System.Windows.Forms;

namespace FarmaFlow.Migration.Desktop;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--smoke-test-postgres", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string postgresBin = Path.Combine(AppContext.BaseDirectory, "postgres", "bin");
                MigrationPipeline.ValidateRuntime(postgresBin);
                string root = Path.Combine(Path.GetTempPath(), $"farmaflow-postgres-smoke-{Guid.NewGuid():N}");
                await using LocalPostgresCluster cluster = await LocalPostgresCluster.StartAsync(postgresBin, root, CancellationToken.None);
                Environment.ExitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            return;
        }
        Application.Run(new MigrationWizardForm());
    }
}
