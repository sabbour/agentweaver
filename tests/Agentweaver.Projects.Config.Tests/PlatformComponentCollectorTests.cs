using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>Tests collecting platform component reports from owner services.</summary>
public sealed class PlatformComponentCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static PlatformComponentOwnerReport Report(string owner, params string[] areas) =>
        new(owner, Now, areas.Select(area => PlatformComponentReportBuilder.Area(area, [], [])).ToArray());

    [Fact]
    public async Task CollectReturnsEveryAreaAndMarksFailedOrSlowOwnersUnknown()
    {
        var client = new FakeOwnerClient(async (owner, token) =>
        {
            switch (owner)
            {
                case PlatformComponentOwners.Knowledge:
                    return Report(owner, PlatformComponentAreaIds.Memory);
                case PlatformComponentOwners.Orchestrator:
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException();
                case PlatformComponentOwners.IdentityBroker:
                    return Report(PlatformComponentOwners.Knowledge, PlatformComponentAreaIds.Secrets);
                default:
                    throw new HttpRequestException("down");
            }
        });
        var collector = new PlatformComponentCollector(
            client, TimeProvider.System, NullLogger<PlatformComponentCollector>.Instance)
        {
            OwnerTimeout = TimeSpan.FromMilliseconds(100),
        };

        var report = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(PlatformComponentCatalog.Areas.Select(area => area.Area), report.Areas.Select(area => area.Area));
        Assert.Equal(PlatformComponentOwners.Reporting.Length, client.Calls.Count);
        var memory = report.Areas.Single(area => area.Area == PlatformComponentAreaIds.Memory);
        Assert.Equal(PlatformComponentReadiness.Disabled, memory.Readiness);
        foreach (var area in report.Areas.Where(area =>
            area.Owner is not PlatformComponentOwners.Knowledge and not PlatformComponentOwners.None))
        {
            Assert.Equal(PlatformComponentReadiness.Unknown, area.Readiness);
            Assert.Equal(PlatformComponentReasons.OwnerUnavailable, area.Reason);
        }
    }

    [Fact]
    public async Task CollectRethrowsCallerCancellation()
    {
        var collector = new PlatformComponentCollector(
            new FakeOwnerClient(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            }),
            TimeProvider.System,
            NullLogger<PlatformComponentCollector>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(cancellation.Token));
    }

    [Fact]
    public void OptionsAreOptionalAndRejectInvalidValues()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value))).Build();

        var empty = PlatformComponentCollectorOptions.Read(Config());
        Assert.Null(empty.TokenFile);
        Assert.Empty(empty.Owners);

        var tokenFile = Path.Combine(Path.GetTempPath(), "token");
        var options = PlatformComponentCollectorOptions.Read(Config(
            ("PlatformComponents:ServiceToken:TokenFile", tokenFile),
            ("PlatformComponents:Owners:knowledge:BaseAddress", "https://knowledge.example/")));
        Assert.Equal(new Uri("https://knowledge.example/"), options.Owners[PlatformComponentOwners.Knowledge]);

        Assert.Throws<InvalidOperationException>(() => PlatformComponentCollectorOptions.Read(Config(
            ("PlatformComponents:ServiceToken:TokenFile", "relative/token"))));
        Assert.Throws<InvalidOperationException>(() => PlatformComponentCollectorOptions.Read(Config(
            ("PlatformComponents:Owners:knowledge:BaseAddress", "http://knowledge.example/"))));
        Assert.Throws<InvalidOperationException>(() => PlatformComponentCollectorOptions.Read(Config(
            ("PlatformComponents:Owners:knowledge:BaseAddress", "https://knowledge.example/path"))));
    }

    [Fact]
    public async Task HttpClientSendsServiceTokenAndValidatesOwner()
    {
        var tokenFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tokenFile, "service-token\n");
            var options = new PlatformComponentCollectorOptions(tokenFile,
                new Dictionary<string, Uri> { [PlatformComponentOwners.Knowledge] = new("https://knowledge.example/") });
            HttpRequestMessage? seen = null;
            var body = JsonSerializer.Serialize(Report(PlatformComponentOwners.Knowledge, PlatformComponentAreaIds.Memory),
                JsonSerializerOptions.Web);
            using var client = new HttpPlatformComponentOwnerClient(options, () => new HttpClient(new StubHandler(request =>
            {
                seen = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            })));

            var report = await client.GetAsync(PlatformComponentOwners.Knowledge, CancellationToken.None);

            Assert.Equal(PlatformComponentOwners.Knowledge, report.Owner);
            Assert.Equal("https://knowledge.example/internal/platform/components", seen!.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
            Assert.Equal("service-token", seen.Headers.Authorization.Parameter);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetAsync(PlatformComponentOwners.Orchestrator, CancellationToken.None));

            using var wrongOwner = new HttpPlatformComponentOwnerClient(options, () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(
                        Report(PlatformComponentOwners.Orchestrator), JsonSerializerOptions.Web)),
                })));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wrongOwner.GetAsync(PlatformComponentOwners.Knowledge, CancellationToken.None));

            using var tooLarge = new HttpPlatformComponentOwnerClient(options, () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 70 * 1024)) })));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tooLarge.GetAsync(PlatformComponentOwners.Knowledge, CancellationToken.None));

            using var denied = new HttpPlatformComponentOwnerClient(options, () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden))));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                denied.GetAsync(PlatformComponentOwners.Knowledge, CancellationToken.None));
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    private sealed class FakeOwnerClient(
        Func<string, CancellationToken, Task<PlatformComponentOwnerReport>> respond) : IPlatformComponentOwnerClient
    {
        public List<string> Calls { get; } = [];

        public Task<PlatformComponentOwnerReport> GetAsync(string owner, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(owner);
            return respond(owner, cancellationToken);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
