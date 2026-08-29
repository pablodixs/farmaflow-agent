using System.Windows.Forms;

namespace FarmaFlow.Server.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ServerSetupForm());
    }
}
