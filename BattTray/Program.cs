using BattTray.Settings;
using BattTray.Tray;

namespace BattTray
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Windows starting the app is the only launch nobody asked to see: it happens
            // while the user is busy logging in, and an app that lives in the tray should
            // not interrupt that. Starting the exe by hand is a deliberate act, and a tray
            // icon appearing among a dozen others is easy to miss, so that launch answers
            // with the settings dialog — visible proof the app is now running.
            bool startedByWindows = args.Contains(AutoStart.StartupSwitch, StringComparer.OrdinalIgnoreCase);

            using var instance = SingleInstance.TryAcquire();
            if (instance is null)
            {
                // Someone double-clicking the exe of an app they have forgotten is already
                // running deserves the same answer as someone starting it fresh, so the
                // request is handed to the instance that has the tray icon. A duplicate
                // autostart is not a person asking for anything, and stays quiet.
                if (!startedByWindows)
                    SingleInstance.RequestSettings();

                return;
            }

            // Entries written before the switch existed cannot say which of the two this is.
            AutoStart.UpgradeCommand();

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // Make the tray menu follow the system light/dark setting; without this it is
            // always light, which looks wrong next to a dark taskbar.
            Application.SetColorMode(SystemColorMode.System);

            var context = new TrayApplicationContext(showSettings: !startedByWindows);

            // After the context is built, not before: constructing its menu is what installs
            // the synchronization context that carries the request back to this thread.
            instance.StartListening(context.ShowSettingsOnRequest);

            Application.Run(context);
        }
    }
}
