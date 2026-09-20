namespace PCleaner.Core.Model;

/// <summary>
/// A single, user-selectable cleanup operation. Rules are immutable descriptions; the engine decides how to run them.
/// </summary>
public sealed class CleanupRule
{
    /// <summary>Stable identifier, used to persist the user's selection (for example <c>windows.temp.user</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Short display name.</summary>
    public required string Name { get; init; }

    /// <summary>What is cleaned and why it is safe.</summary>
    public required string Description { get; init; }

    public required RuleCategory Category { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;

    /// <summary>
    /// Sub group inside the category, for example the browser profile ("Brave — Personal") or "Graphics drivers".
    /// </summary>
    public string Group { get; init; } = string.Empty;

    public RuleAction Action { get; init; } = RuleAction.DeleteFiles;

    /// <summary>File system targets for <see cref="RuleAction.DeleteFiles"/>.</summary>
    public IReadOnlyList<PathTarget> Targets { get; init; } = [];

    /// <summary>Database files for <see cref="RuleAction.PurgeDatabaseRows"/>.</summary>
    public IReadOnlyList<DatabaseTarget> Databases { get; init; } = [];

    /// <summary>Recent-file lists and session stores for <see cref="RuleAction.ForgetHistory"/>.</summary>
    public IReadOnlyList<HistoryTarget> History { get; init; } = [];

    /// <summary>Whether the rule needs an elevated process to have any effect.</summary>
    public bool RequiresAdministrator { get; init; }

    /// <summary>Windows services that must be stopped while the rule runs (they are restarted afterwards).</summary>
    public IReadOnlyList<string> ServicesToStop { get; init; } = [];

    /// <summary>
    /// Process names (without extension) that must not be running while the rule executes, e.g. <c>chrome</c>.
    /// The engine skips the rule (and explains why) instead of deleting files under a running application.
    /// </summary>
    public IReadOnlyList<string> ConflictingProcesses { get; init; } = [];

    /// <summary>
    /// Files that the owning application holds open exclusively while it runs (for example Firefox's
    /// <c>parent.lock</c>). If one of them cannot be opened with shared access the rule is treated as blocked,
    /// which also catches portable builds and processes with unexpected names.
    /// </summary>
    public IReadOnlyList<string> LockProbeFiles { get; init; } = [];

    /// <summary>
    /// False when an application that had to be closed for this rule should stay closed afterwards - an editor
    /// whose saved session was just forgotten would only come back with an empty window.
    /// </summary>
    public bool RelaunchClosedApplications { get; init; } = true;

    /// <summary>Extra data used by non file based actions (event log names, Disk Cleanup handler key name, ...).</summary>
    public string? ActionArgument { get; init; }

    /// <summary>Human readable reference explaining why the location is safe to clean.</summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Optional position inside the group (lower first). Rules without a hint sort after the hinted ones, by risk
    /// and name. Used to lead a group with its most general item ("Temporary files" before ".NET usage logs").
    /// </summary>
    public int? Order { get; init; }

    /// <summary>True when the rule should be selected on first run.</summary>
    public bool EnabledByDefault => Risk == RiskLevel.Safe;

    public override string ToString() => $"{Id} ({Risk})";
}