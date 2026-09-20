using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using PCleaner.App.ViewModels;
using PCleaner.App.Views;
using PCleaner.Core.Logging;
using PCleaner.Core.Settings;
using PCleaner.Core.Windows;

namespace PCleaner.App;

[SupportedOSPlatform("windows")]
public partial class App : Application, IDisposable
{
    private const string MutexName = @"Local\PCleaner.SingleInstance.7E4C1B2A";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.SetObserved();
        };

        var args = e.Args;
#if DEBUG
        EnableBindingTrace();
#endif
        var noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        var alreadyElevatedRestart = args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));

        bool createdNew;
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            // The mutex exists but belongs to a process we cannot open (different integrity level). Treat as running.
            createdNew = false;
            _instanceMutex = null;
        }

        if (!createdNew && (_instanceMutex is null || !TryWaitForPreviousInstance(_instanceMutex, alreadyElevatedRestart ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500))))
        {
            MessageBox.Show("PCleaner is already running.", "PCleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!noElevate && !alreadyElevatedRestart && !Elevation.IsElevated && AppSettings.Load().RequestElevationOnStartup)
        {
            if (Elevation.TryRestartElevated("--elevated"))
            {
                // The elevated instance waits for this mutex; OnExit releases it.
                Shutdown();
                return;
            }
        }

        var window = new MainWindow(new MainViewModel(Dispatcher));
        MainWindow = window;
        window.Show();

        if (args.Any(a => a.Equals("--test-dialog", StringComparison.OrdinalIgnoreCase)))
        {
            // Diagnostics: show the confirmation prompt without any automation involved and record how it was answered.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var vm = (MainViewModel)window.DataContext;
                var result = ConfirmDialog.Confirm(window, "Diagnostic prompt", "Testing whether this prompt is answered without user interaction.", null, null, "Clean now", vm.LogConfirmation);
                vm.LogConfirmation($"result={result}");
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Waits for a previous instance (for example the non-elevated one that just launched us) to release the
    /// single-instance mutex. Returns true when this process now owns the mutex.
    /// </summary>
    private static bool TryWaitForPreviousInstance(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous instance died without releasing - we own it now
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex not owned by this thread - nothing to release.
        }

        Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception.Message + "\n\nDetails were written to the log folder. PCleaner will keep running.",
            "PCleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    public void Dispose()
    {
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        GC.SuppressFinalize(this);
    }

    private static void LogFatal(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        // The application's own FileLog holds today's log file open, so fatal errors get a file of their own.
        try
        {
            var directory = FileLog.GetDefaultDirectory();
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "PCleaner-fatal.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Unhandled exception: {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else we can do.
        }
    }

#if DEBUG
    /// <summary>Writes WPF data-binding warnings/errors to the log folder (debug builds only).</summary>
    private static void EnableBindingTrace()
    {
        try
        {
            var directory = FileLog.GetDefaultDirectory();
            System.IO.Directory.CreateDirectory(directory);
            var listener = new System.Diagnostics.TextWriterTraceListener(System.IO.Path.Combine(directory, "binding-trace.log"));
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.Trace.AutoFlush = true;
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }
#endif
}