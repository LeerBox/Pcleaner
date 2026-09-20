namespace PCleaner.Core.Model;

/// <summary>
/// Describes how "dangerous" a cleanup rule is. Only <see cref="Safe"/> rules are enabled by default.
/// </summary>
public enum RiskLevel
{
    /// <summary>Pure caches / temporary data that Windows or the application rebuilds automatically. Enabled by default.</summary>
    Safe = 0,

    /// <summary>
    /// Safe for the system, but has a visible side effect (for example: a web app must re-download its offline
    /// data, restored tabs are lost, or a first start is slightly slower). Disabled by default.
    /// </summary>
    Moderate = 1,

    /// <summary>Personal traces (history, cookies, recent documents). Cleaning is a privacy choice. Disabled by default.</summary>
    Privacy = 2,
}