namespace PadScreen;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Modalità di servizio, avviata con i diritti di amministratore per registrare le risoluzioni del driver.
        if (args.Length >= 2 && args[0] == "--register-modes")
        {
            try
            {
                var modes = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Split('x'))
                    .Select(p => (int.Parse(p[0]), int.Parse(p[1])))
                    .ToList();
                ParsecModes.Write(modes, 60);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        using var mutex = new Mutex(true, "PadScreen.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("PadScreen è già aperto.", "PadScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
