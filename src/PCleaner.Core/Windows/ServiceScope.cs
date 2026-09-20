using System.Runtime.Versioning;
using System.ServiceProcess;
using PCleaner.Core.Logging;

namespace PCleaner.Core.Windows;

/// <summary>
/// Stops Windows services for the duration of a cleanup and restores their previous state afterwards.
/// Only services that were running are restarted, so a service the user disabled stays stopped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceScope : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly ICleanerLog _log;
    private readonly List<string> _toRestart = [];
    private bool _disposed;

    private ServiceScope(ICleanerLog log)
    {
        _log = log;
    }

    /// <summary>Names of the services that could not be stopped (the caller should treat their files as locked).</summary>
    public IReadOnlyList<string> Failed { get; private set; } = [];

    /// <summary>Stops the given services (best effort) and returns a scope that restarts them on dispose.</summary>
    public static ServiceScope Stop(IEnumerable<string> serviceNames, ICleanerLog log)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        var scope = new ServiceScope(log ?? NullLog.Instance);
        var failed = new List<string>();

        foreach (var name in serviceNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var controller = new ServiceController(name);
                var status = controller.Status;
                if (status == ServiceControllerStatus.Stopped || status == ServiceControllerStatus.StopPending)
                {
                    continue;
                }

                if (!controller.CanStop)
                {
                    failed.Add(name);
                    scope._log.Warn($"Service '{name}' cannot be stopped right now.");
                    continue;
                }

                scope._log.Info($"Stopping service '{name}' ({controller.DisplayName})...");
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                scope._toRestart.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add(name);
                scope._log.Warn($"Could not stop service '{name}': {ex.Message}");
            }
        }

        scope.Failed = failed;
        return scope;
    }

    public static bool IsRunning(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var name in _toRestart)
        {
            try
            {
                using var controller = new ServiceController(name);
                if (controller.Status == ServiceControllerStatus.Stopped)
                {
                    _log.Info($"Restarting service '{name}'...");
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
                }
            }
            catch (Exception ex)
            {
                // Services like wuauserv are trigger/demand started and will come back by themselves.
                _log.Warn($"Could not restart service '{name}': {ex.Message}");
            }
        }
    }
}