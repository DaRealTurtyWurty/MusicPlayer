using System.Configuration;
using System.Data;
using System.Windows;
using System.Diagnostics;
using MusicPlayer.Services;

namespace MusicPlayer
{
    public partial class App : Application
    {
        private readonly bool _suppressStartup;
        public App() { }
        // The regression runner loads this exact XAML component without opening real user data.
        internal App(bool suppressStartup) => _suppressStartup = suppressStartup;
        private Mutex? _instance;
        private bool _ownsInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (_suppressStartup) return;
            DiagnosticLog.Start();
            base.OnStartup(e);
            var libraryKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(SqliteMusicStore.DefaultDatabasePath.ToUpperInvariant())));
            _instance = new Mutex(false, $"Global\\MusicPlayer.Library.{libraryKey}");
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            if (!_ownsInstance)
            {
                MessageBox.Show("MusicPlayer is already running. Close it before restoring the library.", "MusicPlayer");
                Shutdown(1);
                return;
            }
            DispatcherUnhandledException += (_, args) =>
            {
                Trace.TraceError($"Unhandled UI exception: {args.Exception}");
                args.Handled = true;
                ShowFailure();
                Shutdown(1);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Trace.TraceError($"Fatal exception: {args.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, args) => Trace.TraceError($"Unobserved task exception: {args.Exception}");
            try
            {
                if (e.Args is ["--backup-library"])
                {
                    new SqliteMusicStore().LoadLibrary();
                    var path = DatabaseRecovery.Backup(SqliteMusicStore.DefaultDatabasePath);
                    MessageBox.Show($"Library backup saved to:\n{path}", "MusicPlayer backup");
                    Shutdown();
                }
                else if (e.Args is ["--restore-library", var backup])
                {
                    var archive = DatabaseRecovery.Restore(backup, SqliteMusicStore.DefaultDatabasePath);
                    MessageBox.Show($"Library restored. Previous files preserved at:\n{archive}\n\nStart MusicPlayer to open the restored library.", "MusicPlayer recovery");
                    Shutdown();
                }
                else if (e.Args.Length != 0)
                {
                    MessageBox.Show("Supported commands: --backup-library or --restore-library <backup path>", "MusicPlayer");
                    Shutdown(1);
                }
                else new MainWindow().Show();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Startup/recovery failed: {ex}");
                ShowFailure();
                Shutdown(1);
            }
        }

        private static void ShowFailure() => MessageBox.Show(
            $"MusicPlayer could not continue. Your library has not been reset.\n\nDiagnostics: {DiagnosticLog.DirectoryPath}\nBackups: {System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SqliteMusicStore.DefaultDatabasePath)!, "backups")}\n\nSee the recovery instructions in docs/INSTALLATION.md.",
            "MusicPlayer error", MessageBoxButton.OK, MessageBoxImage.Error);

        protected override void OnExit(ExitEventArgs e)
        {
            Trace.TraceInformation($"Exiting MusicPlayer ({e.ApplicationExitCode}).");
            if (_ownsInstance) _instance?.ReleaseMutex();
            _instance?.Dispose();
            base.OnExit(e);
        }
    }
}
