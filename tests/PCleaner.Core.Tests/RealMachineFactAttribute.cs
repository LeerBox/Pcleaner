namespace PCleaner.Core.Tests;

/// <summary>Requires explicit opt-in before a test reads the current user's application data or Windows settings.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealMachineFactAttribute : FactAttribute
{
    public RealMachineFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCLEANER_RUN_REAL_MACHINE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set PCLEANER_RUN_REAL_MACHINE_TESTS=1 to enable tests that inspect real profiles and Windows settings.";
        }
    }
}