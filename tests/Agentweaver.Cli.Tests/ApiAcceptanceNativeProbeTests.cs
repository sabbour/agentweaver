using System.Text.Json.Nodes;
using Agentweaver.Cli;

namespace Agentweaver.Cli.Tests;

public sealed class ApiAcceptanceNativeProbeTests
{
    [Fact]
    public void ImportedNumericAndRestartPassClaimsRemainBlockedAndKeepTheirEvidence()
    {
        JsonObject envelope = RecoveryEnvelope(
            ("numeric-copilot-dispatch-boundaries", "passed"),
            ("checkpoint-restart-duplicate-fence", "passed"));

        IReadOnlyList<JsonObject> rows = ApiAcceptanceSupport.NativeProbeRows(null, envelope);

        Assert.Equal(2, rows.Count);
        foreach (JsonObject row in rows)
        {
            Assert.Equal("blocked", ApiAcceptanceSupport.GetString(row, "status"));
            Assert.Contains("An imported pass claim is not accepted.", ApiAcceptanceSupport.GetString(row, "reason"));
            Assert.Equal("reported evidence", ApiAcceptanceSupport.GetString((JsonObject)row["evidence"]!, "claim"));
            Assert.Equal("not-started", ApiAcceptanceSupport.GetString((JsonObject)row["cleanup"]!, "status"));
        }

        JsonObject result = ApiAcceptanceSupport.Result(
            "passed",
            null,
            [],
            new JsonObject(),
            null,
            rows);

        Assert.Equal("blocked", ApiAcceptanceSupport.GetString(result, "fullP1Status"));
        Assert.Equal(2, result["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public void AggregateResultAlsoBlocksDirectUnverifiedPassClaims()
    {
        JsonObject result = ApiAcceptanceSupport.Result(
            "passed",
            null,
            [],
            new JsonObject(),
            null,
            [
                new JsonObject
                {
                    ["id"] = "numeric-copilot-dispatch-boundaries",
                    ["required"] = true,
                    ["status"] = "passed",
                    ["evidence"] = new JsonObject { ["claim"] = "reported evidence" },
                },
            ]);

        JsonObject numericProbe = ((JsonArray)result["probes"]!)
            .Cast<JsonObject>()
            .Single(probe => ApiAcceptanceSupport.GetString(probe, "id") == "numeric-copilot-dispatch-boundaries");
        Assert.Equal("blocked", ApiAcceptanceSupport.GetString(numericProbe, "status"));
        Assert.Equal("blocked", ApiAcceptanceSupport.GetString(result, "fullP1Status"));
        Assert.Equal(2, result["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public void ExplicitFailureRemainsFailed()
    {
        JsonObject envelope = RecoveryEnvelope(
            ("numeric-copilot-dispatch-boundaries", "failed"),
            ("checkpoint-restart-duplicate-fence", "passed"));

        IReadOnlyList<JsonObject> rows = ApiAcceptanceSupport.NativeProbeRows(null, envelope);
        JsonObject result = ApiAcceptanceSupport.Result("passed", null, [], new JsonObject(), null, rows);

        Assert.Equal("failed", ApiAcceptanceSupport.GetString(rows[0], "status"));
        Assert.Equal("failed", ApiAcceptanceSupport.GetString(result, "fullP1Status"));
        Assert.Equal(1, result["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public void PartialClaimsAndUnrelatedNativePassesKeepTheirStatuses()
    {
        JsonObject recovery = RecoveryEnvelope(
            ("numeric-copilot-dispatch-boundaries", "blocked"),
            ("checkpoint-restart-duplicate-fence", "blocked"));
        IReadOnlyList<JsonObject> recoveryRows = ApiAcceptanceSupport.NativeProbeRows(null, recovery);
        Assert.All(recoveryRows, row => Assert.Equal("blocked", ApiAcceptanceSupport.GetString(row, "status")));
        Assert.Equal("Partial proof is available.", ApiAcceptanceSupport.GetString(recoveryRows[0], "reason"));

        JsonObject native = NativeEnvelope(
            ("storage-owner-isolation", "passed"),
            ("network-policy-datapath", "passed"),
            ("sandbox-owner-lifecycle", "passed"),
            ("agenthost-image-readiness", "passed"));
        IReadOnlyList<JsonObject> nativeRows = ApiAcceptanceSupport.NativeProbeRows(native, null);
        Assert.All(nativeRows, row => Assert.Equal("passed", ApiAcceptanceSupport.GetString(row, "status")));
    }

    private static JsonObject RecoveryEnvelope(params (string Name, string Status)[] results) =>
        Envelope("p1-dispatch-recovery-acceptance", results, recovery: true);

    private static JsonObject NativeEnvelope(params (string Name, string Status)[] results) =>
        Envelope("p1-native-acceptance", results, recovery: false);

    private static JsonObject Envelope(
        string kind,
        IEnumerable<(string Name, string Status)> results,
        bool recovery)
    {
        JsonArray probes = [];
        foreach ((string name, string status) in results)
        {
            probes.Add(new JsonObject
            {
                ["name"] = name,
                ["status"] = status,
                ["reason"] = status == "blocked" ? "Partial proof is available." : null,
                ["evidence"] = new JsonObject { ["claim"] = "reported evidence" },
                ["cleanup"] = recovery
                    ? new JsonObject
                    {
                        ["status"] = "not-started",
                        ["createdResourceIds"] = new JsonArray(),
                    }
                    : new JsonObject
                    {
                        ["status"] = "not-required",
                        ["actions"] = new JsonArray(),
                    },
            });
        }

        string overallStatus = probes.Cast<JsonObject>().Any(probe => ApiAcceptanceSupport.GetString(probe, "status") == "failed")
            ? "failed"
            : probes.Cast<JsonObject>().Any(probe => ApiAcceptanceSupport.GetString(probe, "status") == "blocked")
                ? "blocked"
                : "passed";
        return ApiAcceptanceSupport.CreateNativeProbeEnvelope(new JsonObject
        {
            ["kind"] = kind,
            ["schemaVersion"] = 1L,
            ["overallStatus"] = overallStatus,
            ["startedAt"] = "2026-11-11T00:00:00Z",
            ["completedAt"] = "2026-11-11T00:00:01Z",
            ["probes"] = probes,
        });
    }
}
