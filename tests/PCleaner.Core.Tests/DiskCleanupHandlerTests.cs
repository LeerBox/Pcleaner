using PCleaner.Core.Logging;
using PCleaner.Core.Windows;
using Xunit.Abstractions;

namespace PCleaner.Core.Tests;

public sealed class DiskCleanupHandlerTests
{
    private readonly ITestOutputHelper _output;

    public DiskCleanupHandlerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Handlers_are_enumerated_from_the_registry()
    {
        var handlers = DiskCleanupHandlers.Enumerate();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, h => Assert.NotEqual(Guid.Empty, h.Clsid));
        Assert.Contains(handlers, h => h.KeyName == "Temporary Files");
        Assert.All(handlers, h => Assert.False(string.IsNullOrWhiteSpace(h.DisplayName)));
        foreach (var h in handlers)
        {
            _output.WriteLine($"{h.Priority,4}  {h.KeyName,-40} {h.DisplayName,-40} {h.Clsid}");
        }
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task GetSpaceUsed_works_through_com_interop_for_per_user_handlers()
    {
        // These handlers operate on per-user data and can be queried without elevation. GetSpaceUsed is read-only.
        var candidates = new[] { "D3D Shader Cache", "Thumbnail Cache", "Temporary Files", "Internet Cache Files" };
        var handlers = DiskCleanupHandlers.Enumerate().Where(h => candidates.Contains(h.KeyName, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(handlers);

        var successes = 0;
        foreach (var handler in handlers)
        {
            var scan = await DiskCleanupHandlers.ScanAsync(handler, NullLog.Instance, CancellationToken.None);
            _output.WriteLine($"{handler.KeyName,-25} succeeded={scan.Succeeded} bytes={scan.Bytes:N0} default={scan.EnabledByDefault} error={scan.Error}");
            if (scan.Succeeded)
            {
                successes++;
                Assert.True(scan.Bytes >= 0);
            }
        }

        // At least one handler must round-trip through Initialize/GetSpaceUsed/Deactivate; a wrong vtable would
        // have crashed the process before we got here.
        Assert.True(successes > 0, "No Disk Cleanup handler could be queried.");
    }
}