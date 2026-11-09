using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Cli;
using Agentweaver.Gateway;
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

/// <summary>
/// Keeps tests/fixtures/critical-paths.json in step with the Gateway routes and the acceptance journeys.
/// A new route on a watched critical path, or a route that closes a known gap, fails this test until
/// the journey and the manifest are updated.
/// </summary>
public sealed class CriticalPathCoverageTests
{
    private static readonly JsonObject Manifest = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "critical-paths.json")))!.AsObject();

    private static readonly GatewayRoute[] Routes = [.. GatewayRouteCatalog.Routes];

    public static TheoryData<string> PathIds() =>
        new(Manifest["criticalPaths"]!.AsArray().Select(path => path!["id"]!.GetValue<string>()));

    [Theory]
    [MemberData(nameof(PathIds))]
    public void EveryWatchedRouteIsCoveredOrExplicitlyOutOfPath(string id)
    {
        JsonObject path = CriticalPath(id);
        HashSet<string> covered = Strings(path["coveredOperations"]);
        HashSet<string> outOfPath = OutOfPath(path).Keys.ToHashSet(StringComparer.Ordinal);
        string[] prefixes = [.. Strings(path["watchRoutePrefixes"])];
        Assert.NotEmpty(prefixes);
        Assert.Equal([], prefixes.Where(prefix => !Routes.Any(route => HasPrefix(route.PublicPath, prefix))).ToArray());

        string[] unclassified = [.. Routes
            .Where(route => prefixes.Any(prefix => HasPrefix(route.PublicPath, prefix)))
            .Select(route => route.OperationId)
            .Where(operation => !covered.Contains(operation) && !outOfPath.Contains(operation))
            .Distinct()
            .Order(StringComparer.Ordinal)];
        Assert.True(unclassified.Length == 0,
            $"Critical path {id} has watched routes that the journey does not cover. Add them to the " +
            $"{path["journey"]} journey and coveredOperations, or to outOfPath with a reason: {string.Join(", ", unclassified)}");
    }

    [Theory]
    [MemberData(nameof(PathIds))]
    public void CoveredOperationsAreRealAndRequiredByTheJourney(string id)
    {
        JsonObject path = CriticalPath(id);
        string journey = path["journey"]!.GetValue<string>();
        HashSet<string> routeOperations = Routes.Select(route => route.OperationId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> required = ApiAcceptanceJourney.RequiredOperations(journey, null);
        HashSet<string> covered = Strings(path["coveredOperations"]);
        Assert.NotEmpty(covered);

        Assert.Equal([], covered.Where(operation => !routeOperations.Contains(operation)).ToArray());
        Assert.Equal([], covered.Where(operation => !required.Contains(operation)).ToArray());
        if (journey == ApiAcceptanceSupport.FullFlowJourney)
        {
            Assert.Equal([], ApiAcceptanceFullFlow.RequiredOperations.Where(operation => !covered.Contains(operation)).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(PathIds))]
    public void OutOfPathEntriesAreCurrentAndExplained(string id)
    {
        JsonObject path = CriticalPath(id);
        HashSet<string> covered = Strings(path["coveredOperations"]);
        string[] prefixes = [.. Strings(path["watchRoutePrefixes"])];
        foreach ((string operation, string reason) in OutOfPath(path))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{operation} needs a reason.");
            Assert.DoesNotContain(operation, covered);
            Assert.True(Routes.Any(route => route.OperationId == operation
                    && prefixes.Any(prefix => HasPrefix(route.PublicPath, prefix))),
                $"{operation} is not a watched route. Remove the stale outOfPath entry.");
        }
    }

    [Theory]
    [MemberData(nameof(PathIds))]
    public void KnownGapsStayOpenUntilTheJourneyCoversThem(string id)
    {
        JsonObject path = CriticalPath(id);
        JsonObject[] gaps = [.. path["gaps"]!.AsArray().OfType<JsonObject>()];
        foreach (JsonObject gap in gaps)
        {
            string prefix = gap["routePrefix"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(gap["reason"]?.GetValue<string>()));
            string[] arrived = [.. Routes.Where(route => ContainsGap(route.PublicPath, prefix)).Select(route => route.OperationId)];
            Assert.True(arrived.Length == 0,
                $"The {gap["stage"]} gap is now served by {string.Join(", ", arrived)}. Extend the journey to cover it, " +
                "move the prefix to watchRoutePrefixes, and remove the gap.");
        }

        JsonArray reported = ApiAcceptanceFullFlow.CriticalPathGaps();
        Assert.Equal(
            gaps.Select(gap => $"{gap["stage"]}|{gap["routePrefix"]}").Order(StringComparer.Ordinal),
            reported.OfType<JsonObject>().Select(gap => $"{gap["stage"]}|{gap["missingRoutePrefix"]}").Order(StringComparer.Ordinal));
    }

    private static JsonObject CriticalPath(string id) =>
        Manifest["criticalPaths"]!.AsArray().OfType<JsonObject>().Single(path => path["id"]!.GetValue<string>() == id);

    private static HashSet<string> Strings(JsonNode? values) =>
        (values as JsonArray ?? []).Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> OutOfPath(JsonObject path) =>
        (path["outOfPath"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(
            entry => entry["operation"]!.GetValue<string>(),
            entry => entry["reason"]?.GetValue<string>() ?? "",
            StringComparer.Ordinal);

    // Catalog paths carry the /api/v1 prefix; route constraints such as {proposalId:guid} do not change the path.
    private static string Normalize(string publicPath)
    {
        Assert.StartsWith(GatewayRouteCatalog.VersionPrefix + "/", publicPath);
        return Regex.Replace(publicPath[GatewayRouteCatalog.VersionPrefix.Length..],
            @"\{([^}:]+):[^}]+\}", "{$1}", RegexOptions.CultureInvariant);
    }

    // A gap matches anywhere in the path, so /projects/{projectId}/workflows or actions/dispatch_task also close it.
    private static bool ContainsGap(string publicPath, string gap) =>
        Normalize(publicPath).Contains(gap, StringComparison.Ordinal);

    private static bool HasPrefix(string publicPath, string prefix)
    {
        string normalized = Normalize(publicPath);
        return normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}
