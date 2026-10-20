using System.Collections.Immutable;
using System.Numerics;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>A fixed provisioned-throughput charge for one reservation period, taken from a versioned rate card.</summary>
/// <param name="RateCardId">Rate-card identity that published the charge.</param>
/// <param name="RateCardVersion">Immutable rate-card version.</param>
/// <param name="Unit">ISO 4217 currency code.</param>
/// <param name="Amount">Period charge; it must be exact at <paramref name="MinorUnitDigits"/>.</param>
/// <param name="MinorUnitDigits">Number of currency minor-unit digits, from 0 through 6.</param>
/// <param name="StartsAt">Inclusive period start.</param>
/// <param name="EndsAt">Exclusive period end.</param>
public sealed record AzureProvisionedThroughputCharge(
    string RateCardId, string RateCardVersion, string Unit, decimal Amount, int MinorUnitDigits,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Token usage for one ledger event that ran on the provisioned deployment during the period.</summary>
public sealed record AzureProvisionedThroughputUsage(Guid EventId, long? InputTokens, long? OutputTokens);

/// <summary>The share of the period charge assigned to one ledger event.</summary>
/// <param name="EventId">Ledger event that received the share.</param>
/// <param name="Tokens">Input plus output tokens used for the share.</param>
/// <param name="FloorMinorUnits">Exact floor of charge × tokens ÷ total tokens, in minor units.</param>
/// <param name="RemainderMinorUnits">Minor units added by largest-remainder rounding (0 or 1).</param>
/// <param name="Amount">Allocated amount in the charge unit.</param>
public sealed record AzureProvisionedThroughputShare(
    Guid EventId, long Tokens, long FloorMinorUnits, long RemainderMinorUnits, decimal Amount);

/// <summary>
/// Report-only allocation result. A null <see cref="UnpricedReason"/> means the shares add up to the
/// charge exactly. The allocation never changes committed ledger rows or their prices.
/// </summary>
public sealed record AzureProvisionedThroughputAllocation(
    AzureProvisionedThroughputCharge Charge,
    long TotalTokens,
    long ChargeMinorUnits,
    ImmutableArray<AzureProvisionedThroughputShare> Shares,
    string RoundingMethod,
    string? UnpricedReason);

/// <summary>Allocates a provisioned-throughput charge to ledger events by token usage share with exact integer arithmetic.</summary>
public static class AzureProvisionedThroughputAllocator
{
    /// <summary>Rounding evidence recorded with every allocation.</summary>
    public const string RoundingMethod = "floor-then-largest-remainder-by-event-id";

    /// <summary>
    /// Splits the charge in proportion to input plus output tokens. Each event first gets the floor of its
    /// exact share in minor units. The remaining minor units go to the largest remainders, with ties broken
    /// by event ID, so the shares always add up to the charge.
    /// </summary>
    /// <exception cref="ArgumentException">The charge or the usage set is malformed.</exception>
    public static AzureProvisionedThroughputAllocation Allocate(
        AzureProvisionedThroughputCharge charge, IReadOnlyCollection<AzureProvisionedThroughputUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(charge);
        ArgumentNullException.ThrowIfNull(usage);
        if (string.IsNullOrWhiteSpace(charge.RateCardId) || string.IsNullOrWhiteSpace(charge.RateCardVersion) ||
            charge.Unit is not { Length: 3 } || !charge.Unit.All(character => character is >= 'A' and <= 'Z') ||
            charge.MinorUnitDigits is < 0 or > 6 || charge.Amount < 0 || charge.EndsAt <= charge.StartsAt)
            throw new ArgumentException("The provisioned-throughput charge is invalid.", nameof(charge));
        var scale = Pow10(charge.MinorUnitDigits);
        var scaled = charge.Amount * scale;
        if (scaled != decimal.Truncate(scaled) || scaled > long.MaxValue)
            throw new ArgumentException(
                "The provisioned-throughput charge is not exact at its minor-unit precision.", nameof(charge));
        var chargeMinor = (long)scaled;
        var events = new HashSet<Guid>();
        foreach (var item in usage)
            if (item is null || item.EventId == Guid.Empty || !events.Add(item.EventId) ||
                item.InputTokens < 0 || item.OutputTokens < 0)
                throw new ArgumentException("The provisioned-throughput usage set is invalid.", nameof(usage));

        if (usage.Any(item => item.InputTokens is null || item.OutputTokens is null))
            return Unpriced(charge, chargeMinor, "token-measurements-missing");
        var tokens = usage.ToDictionary(item => item.EventId,
            item => checked(item.InputTokens!.Value + item.OutputTokens!.Value));
        var total = tokens.Values.Aggregate(0L, (sum, value) => checked(sum + value));
        if (total == 0)
            return Unpriced(charge, chargeMinor, "provisioned-throughput-usage-missing");

        var floors = new Dictionary<Guid, long>();
        var remainders = new List<(Guid EventId, BigInteger Remainder)>();
        foreach (var (eventId, count) in tokens)
        {
            var product = new BigInteger(chargeMinor) * count;
            floors[eventId] = (long)BigInteger.DivRem(product, total, out var remainder);
            remainders.Add((eventId, remainder));
        }
        var leftover = chargeMinor - floors.Values.Sum();
        var bumped = remainders
            .OrderByDescending(item => item.Remainder)
            .ThenBy(item => item.EventId.ToString("D"), StringComparer.Ordinal)
            .Take((int)leftover)
            .Select(item => item.EventId)
            .ToHashSet();
        var shares = tokens
            .OrderBy(item => item.Key.ToString("D"), StringComparer.Ordinal)
            .Select(item =>
            {
                var extra = bumped.Contains(item.Key) ? 1L : 0L;
                return new AzureProvisionedThroughputShare(
                    item.Key, item.Value, floors[item.Key], extra, (floors[item.Key] + extra) / scale);
            })
            .ToImmutableArray();
        return new(charge, total, chargeMinor, shares, RoundingMethod, null);
    }

    private static AzureProvisionedThroughputAllocation Unpriced(
        AzureProvisionedThroughputCharge charge, long chargeMinor, string reason) =>
        new(charge, 0, chargeMinor, [], RoundingMethod, reason);

    private static decimal Pow10(int digits)
    {
        var value = 1m;
        for (var index = 0; index < digits; index++)
            value *= 10m;
        return value;
    }
}
