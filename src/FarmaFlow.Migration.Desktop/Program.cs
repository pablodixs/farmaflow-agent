using System.Windows.Forms;

namespace FarmaFlow.Migration.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MigrationWizardForm());
    }
}
