using BattTray.Tray;

namespace BattTray
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // A second tray icon for the same devices would only be confusing.
            using var instanceLock = new Mutex(
                initiallyOwned: true, @"Local\BattTray.SingleInstance", out bool isFirstInstance);
            if (!isFirstInstance)
                return;

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // Make the tray menu follow the system light/dark setting; without this it is
            // always light, which looks wrong next to a dark taskbar.
            Application.SetColorMode(SystemColorMode.System);

            Application.Run(new TrayApplicationContext());
        }
    }
}
