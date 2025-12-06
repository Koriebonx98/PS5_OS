using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PS5_OS
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Global exception handlers to capture startup crashes before UI appears.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);

            // Start SteamDb update in background (fire-and-forget).
            // Runs while the intro (if present) plays so it doesn't block startup UI.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SteeamDB.InitializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Use existing logging helper so failures are recorded
                    LogException(ex, "SteeamDB.InitializeAsync");
                }
            });

            // Tie application lifetime to the main window that is shown last.
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var introPath = Path.Combine(AppContext.BaseDirectory, "Data", "Intro", "Intro.mp4");
            if (File.Exists(introPath))
            {
                // Make the intro window the application's MainWindow while it plays.
                var intro = new IntroWindow();
                MainWindow = intro;
                intro.Show();
                return;
            }

            // No intro file — show login
            ShowMainWindow();
        }

        // Public so IntroWindow can call it after playback finishes.
        // This now shows the LoginPage first; LoginPage will navigate to Dashboard after successful login.
        public void ShowMainWindow()
        {
            var main = new MainWindow
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowState = WindowState.Maximized,
                Topmost = false
            };

            // Show login first; LoginPage code-behind already switches to Dashboard on successful login.
            main.Content = new LoginPage();

            // Make this the application's main window (important for ShutdownMode)
            MainWindow = main;
            main.Show();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception, "DispatcherUnhandledException");
            // Prevent default crash dialog so we can provide a friendly message
            MessageBox.Show($"An unexpected error occurred: {e.Exception.Message}\n\nSee crash.log in application folder for details.", "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            ShutdownIfNeeded();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogException(ex, "CurrentDomain_UnhandledException");
            MessageBox.Show($"A fatal error occurred: {ex?.Message ?? "unknown"}\n\nSee crash.log in application folder for details.", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownIfNeeded();
        }

        // Match nullable signature of EventHandler<T> in modern .NET (sender may be null)
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException(e.Exception, "TaskScheduler_UnobservedTaskException");
            e.SetObserved();
        }

        private static void LogException(Exception? ex, string source)
        {
            try
            {
                var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                using var sw = new StreamWriter(logFile, append: true);
                sw.WriteLine("-----");
                sw.WriteLine(DateTime.UtcNow.ToString("u") + "  Source: " + source);
                if (ex != null)
                {
                    sw.WriteLine(ex.ToString());
                }
                else
                {
                    sw.WriteLine("Exception object was null.");
                }
                sw.Flush();
            }
            catch
            {
                // swallow logging errors to avoid recursive failures
            }
        }

        private static void ShutdownIfNeeded()
        {
            try
            {
                // Give user a chance to read message then exit cleanly
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try { Application.Current?.Shutdown(); } catch { }
                });
            }
            catch { }
        }
    }
}