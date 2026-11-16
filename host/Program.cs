namespace SideScreen;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--install-driver"))
        {
            var err = DriverInstaller.Install();
            if (err != null)
            {
                MessageBox.Show("The display driver could not be installed:\n\n" + err, "SideScreen",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }

        // One instance only. Starting it again just brings the running one back from the tray;
        // "SideScreen.exe --exit" asks the running one to quit cleanly.
        using var single = new Mutex(true, "SideScreen.SingleInstance", out bool first);
        if (!first)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(args.Contains("--exit") ? MainForm.ExitSignal : MainForm.ShowSignal);
                signal.Set();
            }
            catch { }
            return 0;
        }
        if (args.Contains("--exit")) return 0;

        Application.Run(new MainForm(args.Contains("--tray"))
        {
            AutoStart = args.Contains("--start"),
            AutoMirror = args.Contains("--mirror"),
        });
        return 0;
    }
}
