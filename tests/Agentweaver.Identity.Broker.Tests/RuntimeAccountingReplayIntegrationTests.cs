using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private async Task<RuntimeNativeTurnAccountingReceipt> VerifyNativeAccountingLostReplyAsync(
        AuthorizedRuntimeSession session, RuntimeActorAuthorization actor,
        RuntimeNativeTurnRecordedReceipt recorded, ImmutableArray<RuntimeUsageCostReceiptReference> references,
        RuntimeCredentialProof staleProof, string staleCredential,
        Dictionary<string, Func<HttpMessageHandler>> routes, ConcurrentQueue<string> failures,
        string schema, CancellationToken cancellationToken)
    {
        Assert.NotEmpty(references);
        var snapshotRequest = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        var eventsRoute = routes["events.test"];
        var snapshots = new List<RuntimeUsageCostSnapshotReceipt>();
        routes["events.test"] = () => new RuntimeServiceRouter(
            new Dictionary<string, Func<HttpMessageHandler>> { ["events.test"] = eventsRoute }, failures,
            async (request, response, token) =>
            {
                if (!response.IsSuccessStatusCode ||
                    request.RequestUri?.AbsolutePath.EndsWith("/usage/copilot-cost-snapshot",
                        StringComparison.Ordinal) != true)
                    return;
                var snapshot = Assert.IsType<RuntimeUsageCostSnapshotReceipt>(
                    await response.Content.ReadFromJsonAsync<RuntimeUsageCostSnapshotReceipt>(
                        CoordinationJsonOptions, token));
                if (snapshots.Count == 1)
                {
                    // A later ledger total need not equal the snapshot returned by the lost response.
                    var totals = snapshot.CopilotTotals;
                    Assert.Empty(totals.Amounts);
                    Assert.Empty(totals.Agents);
                    var amount = new UsageAmountTotal(SdkMeterSources.CopilotNanoAiu, "AIC", 0m, 0, 1);
                    snapshot = snapshot with
                    {
                        CopilotTotals = totals with
                        {
                            Events = totals.Events + 1,
                            IsFullyPriced = false,
                            Agents = totals.Agents.Add(new("controlled-other-agent", 1, null, null,
                                null, null, null, null, false, [amount])),
                            Amounts = [amount]
                        }
                    };
                }
                RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, snapshotRequest);
                snapshots.Add(snapshot);
                response.Content.Dispose();
                response.Content = JsonContent.Create(snapshot, options: CoordinationJsonOptions);
            });
        try
        {
            using var dropped = new DropCommittedAccountingResponseHandler(new RuntimeServiceRouter(routes, failures));
            using var client = new HttpClient(dropped);
            var source = new RuntimeUsageSourceHttpClient(client, new("https://orchestrator.test/"), actor);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                source.CompleteNativeTurnAsync(session, recorded, references, cancellationToken));
            var first = Assert.IsType<RuntimeNativeTurnAccountingReceipt>(dropped.CommittedReceipt);
            var saved = await ReadCompletionAsync();
            Assert.Equal("completed", saved.State);
            Assert.Equal("unpriced", saved.Status);
            Assert.Equal(recorded.OwnerRevision + 1, saved.Revision);
            Assert.Equal(first.OwnerRevision, saved.Revision);
            Assert.Equal(references.OrderBy(reference => reference.SourceReceiptId),
                System.Text.Json.JsonSerializer.Deserialize<ImmutableArray<RuntimeUsageCostReceiptReference>>(
                    saved.References, CoordinationJsonOptions));

            var replay = await source.CompleteNativeTurnAsync(session, recorded, references, cancellationToken);
            Assert.Equal(saved, await ReadCompletionAsync());
            Assert.Equal(first.OwnerRevision, replay.OwnerRevision);
            Assert.Equal(references.ToArray(), replay.Snapshot.RepresentedReceipts.ToArray());
            Assert.Equal(2, snapshots.Count);
            Assert.Equal(snapshots[0].CopilotTotals.Events + 1, replay.Snapshot.CopilotTotals.Events);
            Assert.Equivalent(snapshots[1].CopilotTotals, replay.Snapshot.CopilotTotals, strict: true);

            var proof = new RuntimeCredentialHttpRequest(staleProof.GrantId, staleProof.RuntimeInstanceId,
                staleProof.Revision, staleProof.Purpose, staleProof.Audience, staleProof.ConfigurationHash,
                staleCredential, staleProof.Credential.ExpiresAt, Guid.Empty);
            using var denied = await SendJsonAsync(client, HttpMethod.Post,
                "https://orchestrator.test/internal/runtime/turns/accounting", actor.Bearer.GetValue(),
                new RuntimeNativeTurnAccountingRequest(proof, recorded, references));
            await AssertStatusAsync(denied, HttpStatusCode.Forbidden);
            Assert.Equal(saved, await ReadCompletionAsync());
            return replay;
        }
        finally
        {
            routes["events.test"] = eventsRoute;
        }

        async Task<(string State, string References, string Status, long Revision)> ReadCompletionAsync()
        {
            await using var connection = await _nativeDataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"""
                SELECT dispatch_state, acknowledgment_references::text, accounting_status, owner_revision
                FROM "{schema}".maf_execution_dispatches
                WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
                """, connection);
            command.Parameters.AddWithValue("project", session.Registration.Binding.ProjectId);
            command.Parameters.AddWithValue("run", session.Registration.Binding.RunId);
            command.Parameters.AddWithValue("dispatch", recorded.Admission.MessageId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
        }
    }

    private sealed class DropCommittedAccountingResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public RuntimeNativeTurnAccountingReceipt? CommittedReceipt { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (CommittedReceipt is null && response.IsSuccessStatusCode &&
                request.RequestUri?.AbsolutePath == "/internal/runtime/turns/accounting")
            {
                CommittedReceipt = await response.Content.ReadFromJsonAsync<RuntimeNativeTurnAccountingReceipt>(
                    CoordinationJsonOptions, cancellationToken)
                    ?? throw new HttpRequestException("The owner returned no accounting acknowledgment.");
                response.Dispose();
                throw new HttpRequestException("Committed accounting acknowledgment was lost.");
            }
            return response;
        }
    }
}
