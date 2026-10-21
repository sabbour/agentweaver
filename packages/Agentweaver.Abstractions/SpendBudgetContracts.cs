using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Scopes that a spend budget can limit.</summary>
public static class SpendBudgetScopes
{
    /// <summary>All runs in a project during one explicit period.</summary>
    public const string Project = "project";
    /// <summary>One run for its whole lifetime.</summary>
    public const string Run = "run";
    /// <summary>One agent within one run for the run's whole lifetime.</summary>
    public const string Agent = "agent";
}

/// <summary>
/// One spend budget bucket. A bucket is keyed by its scope, optional agent, meter source, unit, and period,
/// so amounts in different units, such as AI credits and a currency, are never added together.
/// </summary>
/// <param name="BudgetId">Stable budget identity. Platform and project layers refer to the same budget by this ID.</param>
/// <param name="Revision">Budget revision. A changed limit or period uses a higher revision.</param>
/// <param name="Scope">One of <see cref="SpendBudgetScopes"/>.</param>
/// <param name="MeterSource">Events meter source, such as <c>copilot.nano_aiu</c>.</param>
/// <param name="Unit">Priced unit that the limits use, such as <c>AIC</c> or <c>USD</c>.</param>
/// <param name="SoftLimit">Optional amount that sends one notification per bucket and period when reached.</param>
/// <param name="HardLimit">Optional amount that new dispatches can't exceed, including outstanding exposure.</param>
/// <param name="ReservePerDispatch">Exposure that each admitted dispatch holds until Events accounts for it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpendBudget(
    string BudgetId,
    long Revision,
    string Scope,
    string MeterSource,
    string Unit,
    decimal? SoftLimit,
    decimal? HardLimit,
    decimal ReservePerDispatch)
{
    /// <summary>Agent ID. Required for agent scope and absent for other scopes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    /// <summary>Explicit period. Required for project scope and absent for run and agent scopes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageSpendPeriod? Period { get; init; }
}

/// <summary>Validates spend budgets and derives their stable bucket keys.</summary>
public static class SpendBudgetContract
{
    /// <summary>Maximum number of budgets in one configuration layer.</summary>
    public const int MaximumBudgets = 32;

    /// <summary>Maximum decimal amount accepted for limits and reserves.</summary>
    public const decimal MaximumAmount = 1_000_000_000_000m;

    /// <summary>Requires a complete, internally consistent budget.</summary>
    /// <exception cref="ArgumentException">The budget is invalid.</exception>
    public static void Validate(SpendBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!IsIdentifier(budget.BudgetId, 128))
            throw new ArgumentException("A spend budget ID must be a nonempty opaque identifier of at most 128 characters.");
        if (budget.Revision < 1)
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must have a revision of at least 1.");
        if (budget.Scope is not (SpendBudgetScopes.Project or SpendBudgetScopes.Run or SpendBudgetScopes.Agent))
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must use the project, run, or agent scope.");
        if (!IsIdentifier(budget.MeterSource, 256) || !IsIdentifier(budget.Unit, 64))
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must name a meter source and unit.");
        if (budget.Scope == SpendBudgetScopes.Agent != (budget.AgentId is not null) ||
            budget.AgentId is not null && !IsIdentifier(budget.AgentId, 256))
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must name an agent only for agent scope.");
        if (budget.Scope == SpendBudgetScopes.Project != (budget.Period is not null))
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must have an explicit period only for project scope.");
        if (budget.Period is { } period &&
            (!IsIdentifier(period.PeriodId, 128) || period.EndsAt <= period.StartsAt ||
             period.StartsAt.Offset != TimeSpan.Zero || period.EndsAt.Offset != TimeSpan.Zero))
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' period must have an ID and UTC start and end, with the end after the start.");
        if (budget.SoftLimit is null && budget.HardLimit is null)
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must set a soft limit, a hard limit, or both.");
        if (!IsAmount(budget.SoftLimit) || !IsAmount(budget.HardLimit) ||
            budget.ReservePerDispatch < 0 || budget.ReservePerDispatch > MaximumAmount)
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' amounts must be between 0 and {MaximumAmount}.");
        if (budget.SoftLimit is { } soft && budget.HardLimit is { } hard && soft > hard)
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' soft limit cannot exceed its hard limit.");
        if (budget.HardLimit is not null && budget.ReservePerDispatch <= 0)
            throw new ArgumentException($"Spend budget '{budget.BudgetId}' must reserve a positive amount per dispatch when it has a hard limit.");
    }

    /// <summary>Requires valid budgets with unique budget IDs and unique bucket keys.</summary>
    /// <exception cref="ArgumentException">The set is invalid.</exception>
    public static void ValidateSet(ImmutableArray<SpendBudget> budgets)
    {
        if (budgets.IsDefault)
            throw new ArgumentException("The spend budget list is invalid.");
        if (budgets.Length > MaximumBudgets)
            throw new ArgumentException($"At most {MaximumBudgets} spend budgets are allowed.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var budget in budgets)
        {
            Validate(budget);
            if (!ids.Add(budget.BudgetId))
                throw new ArgumentException($"Spend budget '{budget.BudgetId}' is defined more than once.");
            if (!keys.Add(BucketKey(budget)))
                throw new ArgumentException($"Spend budget '{budget.BudgetId}' repeats the bucket of another budget.");
        }
    }

    /// <summary>
    /// Returns the stable bucket key: scope, agent, meter source, unit, and period ID. The run is added by
    /// the admission owner for run and agent scopes.
    /// </summary>
    public static string BucketKey(SpendBudget budget) =>
        string.Join('\u001f', budget.Scope, budget.AgentId ?? string.Empty, budget.MeterSource, budget.Unit,
            budget.Period?.PeriodId ?? string.Empty);

    /// <summary>
    /// Requires that <paramref name="narrowed"/> keeps the identity, bucket, and period of
    /// <paramref name="ceiling"/> and does not raise or remove any of its limits.
    /// </summary>
    /// <exception cref="ArgumentException">The narrowed budget widens the ceiling.</exception>
    public static void RequireNarrowing(SpendBudget ceiling, SpendBudget narrowed)
    {
        Validate(ceiling);
        Validate(narrowed);
        if (ceiling.BudgetId != narrowed.BudgetId || BucketKey(ceiling) != BucketKey(narrowed) ||
            ceiling.Period != narrowed.Period)
            throw new ArgumentException($"Spend budget '{narrowed.BudgetId}' must keep the bucket and period of the platform budget.");
        if (ceiling.SoftLimit is { } soft && (narrowed.SoftLimit is not { } narrowedSoft || narrowedSoft > soft) ||
            ceiling.HardLimit is { } hard && (narrowed.HardLimit is not { } narrowedHard || narrowedHard > hard) ||
            narrowed.ReservePerDispatch < ceiling.ReservePerDispatch)
            throw new ArgumentException($"Spend budget '{narrowed.BudgetId}' cannot widen the platform budget.");
    }

    private static bool IsAmount(decimal? value) => value is null or (>= 0 and <= MaximumAmount);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}

/// <summary>Current project-scope spend budgets that the Projects owner returns for each new dispatch.</summary>
/// <param name="ProjectId">Project that owns the budgets.</param>
/// <param name="ProjectConfigurationRevision">Current project configuration revision.</param>
/// <param name="PlatformRuntimeRevision">Current platform defaults revision.</param>
/// <param name="Budgets">Effective project-scope budgets.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurrentProjectSpendBudgets(
    string ProjectId,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    ImmutableArray<SpendBudget> Budgets);
