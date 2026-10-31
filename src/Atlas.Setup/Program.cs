namespace Atlas.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            SetupEnvironment setupEnvironment = SetupEnvironment.Load();
            Application.Run(new SetupForm(setupEnvironment));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "ATLAS bloqueó el inicio porque el instalador no contiene " +
                "una configuración de ambiente válida.\n\n" +
                exception.Message,
                "ATLAS — instalador no válido",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Environment.ExitCode = 2;
        }
    }
}
