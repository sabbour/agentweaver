using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

public sealed class CriticalPathCoverageTests
{
    private static readonly JsonObject Inventory = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "critical-paths.json")))!.AsObject();
    private static readonly GatewayRoute[] Routes = [.. GatewayRouteCatalog.Routes];

    public static TheoryData<string> PathIds() =>
        new(Inventory["criticalPaths"]!.AsArray().Select(path => path!["id"]!.GetValue<string>()));

    [Theory]
    [MemberData(nameof(PathIds))]
    public void EveryWatchedRouteIsCoveredOrExplainedAsOutOfPath(string id)
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
            $"Critical path {id} has watched routes without a script command or an explained exclusion: {string.Join(", ", unclassified)}");
    }

    [Theory]
    [MemberData(nameof(PathIds))]
    public void CoveredOperationsRunThroughTheNamedCliCase(string id)
    {
        JsonObject path = CriticalPath(id);
        string caseScript = path["caseScript"]!.GetValue<string>();
        Assert.Equal("scripts/acceptance/api/read-only-gateway.mjs", caseScript);

        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Scripts", "acceptance", "api", "read-only-gateway.mjs"));
        HashSet<string> describedOperations = Regex.Matches(
                script,
                @"args\s*:\s*\[\s*'api'\s*,\s*'(?:describe|invoke)'\s*,\s*'(?<operation>[^']+)'",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["operation"].Value)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> covered = Strings(path["coveredOperations"]);
        Assert.NotEmpty(covered);

        Match restCommand = Regex.Match(
            script,
            @"\{\s*id:\s*'rest-authorization-context'\s*,\s*args:\s*\[\s*'rest',(?<args>[\s\S]*?)\]\s*,?\s*\}",
            RegexOptions.CultureInvariant);
        Assert.True(restCommand.Success, "The named case must execute a literal agentweaver rest command.");
        Match method = Regex.Match(restCommand.Groups["args"].Value, @"'--method'\s*,\s*'(?<value>[^']+)'");
        Match url = Regex.Match(restCommand.Groups["args"].Value, @"'--url'\s*,\s*'(?<value>[^']+)'");
        Assert.True(method.Success && url.Success, "The rest command must declare its method and route.");

        foreach (string operation in covered)
        {
            Assert.Contains(operation, describedOperations);
            GatewayRoute route = Routes.Single(item => item.OperationId == operation);
            Assert.Equal(route.Method, method.Groups["value"].Value);
            Assert.Equal(route.PublicPath, url.Groups["value"].Value);
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

    [Fact]
    public void KnownGapsStayOpenUntilTheGatewayAddsTheirRoutes()
    {
        JsonArray gaps = Inventory["knownGaps"]!.AsArray();
        foreach (JsonObject gap in gaps.OfType<JsonObject>())
        {
            string prefix = gap["routePrefix"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(gap["reason"]?.GetValue<string>()));
            string[] arrived = [.. Routes.Where(route => ContainsGap(route.PublicPath, prefix)).Select(route => route.OperationId)];
            Assert.True(arrived.Length == 0,
                $"The {gap["stage"]} gap is now served by {string.Join(", ", arrived)}. Extend a CLI case to cover it and remove the gap.");
        }
    }

    private static JsonObject CriticalPath(string id) =>
        Inventory["criticalPaths"]!.AsArray().OfType<JsonObject>().Single(path => path["id"]!.GetValue<string>() == id);

    private static HashSet<string> Strings(JsonNode? values) =>
        (values as JsonArray ?? []).Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> OutOfPath(JsonObject path) =>
        (path["outOfPath"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(
            entry => entry["operation"]!.GetValue<string>(),
            entry => entry["reason"]?.GetValue<string>() ?? "",
            StringComparer.Ordinal);

    private static string Normalize(string publicPath)
    {
        Assert.StartsWith(GatewayRouteCatalog.VersionPrefix + "/", publicPath);
        return Regex.Replace(publicPath[GatewayRouteCatalog.VersionPrefix.Length..],
            @"\{([^}:]+):[^}]+\}", "{$1}", RegexOptions.CultureInvariant);
    }

    private static bool ContainsGap(string publicPath, string gap) =>
        Normalize(publicPath).Contains(gap, StringComparison.Ordinal);

    private static bool HasPrefix(string publicPath, string prefix)
    {
        string normalized = Normalize(publicPath);
        return normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}
