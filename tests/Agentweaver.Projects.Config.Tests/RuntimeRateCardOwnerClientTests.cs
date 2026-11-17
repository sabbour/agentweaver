using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class RuntimeRateCardOwnerClientTests
{
    private const string CardId = "manual:azure-card";
    private const string Version = "2026-11-10";

    [Fact]
    public async Task VerifiesCanonicalCardWithStrongHashAndEscapedKeySegments()
    {
        var payload = Canonical(ManualCard(CardId, Version));
        var observedPath = string.Empty;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            observedPath = request.RequestUri!.OriginalString;
            return Task.FromResult(Response(payload, StrongTag(payload)));
        }));
        var client = CreateClient(httpClient);

        Assert.True(await client.VerifyAsync(CardId, Version, CancellationToken.None));
        Assert.Contains("/manual%3Aazure-card/2026-11-10", observedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsMissingWeakAndMismatchedEtags()
    {
        var payload = Canonical(ManualCard(CardId, Version));
        foreach (var tag in new string?[] { null, $"W/{StrongTag(payload)}", "\"0000000000000000000000000000000000000000000000000000000000000000\"" })
        {
            using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(Response(payload, tag))));
            Assert.False(await CreateClient(httpClient).VerifyAsync(CardId, Version, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RejectsWrongVersionAndInvalidProvenanceWhenTheBodyHashMatches()
    {
        var wrongVersion = Canonical(ManualCard(CardId, "2026-11-11"));
        using (var wrongVersionClient = new HttpClient(new DelegateHandler((_, _) =>
                   Task.FromResult(Response(wrongVersion, StrongTag(wrongVersion))))))
            Assert.False(await CreateClient(wrongVersionClient).VerifyAsync(CardId, Version, CancellationToken.None));

        var wrongId = Canonical(ManualCard("manual:other-card", Version));
        using (var wrongIdClient = new HttpClient(new DelegateHandler((_, _) =>
                   Task.FromResult(Response(wrongId, StrongTag(wrongId))))))
            Assert.False(await CreateClient(wrongIdClient).VerifyAsync(CardId, Version, CancellationToken.None));

        var invalidProvenance = Canonical(ManualCard(CardId, Version))
            .Replace("https://example.com/prices", "http://example.com/prices", StringComparison.Ordinal);
        using var invalidProvenanceClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(Response(invalidProvenance, StrongTag(invalidProvenance)))));
        Assert.False(await CreateClient(invalidProvenanceClient).VerifyAsync(CardId, Version, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task LeavesCardUnavailableWhenEventsCannotAnswer(HttpStatusCode status)
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status))));
        Assert.False(await CreateClient(httpClient).VerifyAsync(CardId, Version, CancellationToken.None));
    }

    [Fact]
    public async Task TreatsOwnerTimeoutAsUnavailableAndPropagatesCallerCancellation()
    {
        using (var timeoutClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
               {
                   await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                   return new HttpResponseMessage(HttpStatusCode.OK);
               })))
            Assert.False(await CreateClient(timeoutClient, TimeSpan.FromMilliseconds(25))
                .VerifyAsync(CardId, Version, CancellationToken.None));

        using var callerClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(callerClient, TimeSpan.FromSeconds(1))
                .VerifyAsync(CardId, Version, callerCancellation.Token));
    }

    private static RuntimeRateCardOwnerClient CreateClient(HttpClient httpClient, TimeSpan? timeout = null)
    {
        var options = new PlatformComponentCollectorOptions(
            "unused-token-file",
            new Dictionary<string, Uri>(StringComparer.Ordinal)
            {
                [PlatformComponentOwners.EventsAndSessions] = new("https://events.example/"),
            });
        return new RuntimeRateCardOwnerClient(
            httpClient,
            options,
            _ => Task.FromResult<string?>("projects-token"),
            timeout ?? TimeSpan.FromSeconds(1));
    }

    private static string Canonical(RuntimeRateCardReadResponse response) =>
        RuntimeRateCardReadContract.SerializeCanonical(response);

    private static RuntimeRateCardReadResponse ManualCard(string id, string version) =>
        new(
            RuntimeRateCardReadContract.CurrentSchemaVersion,
            new CostRateCard(
                id,
                version,
                SdkMeterSources.ByokTokens,
                "USD",
                1_000_000_000m,
                ImmutableDictionary.CreateRange(StringComparer.Ordinal,
                [
                    KeyValuePair.Create("gpt-4.1|input", 1.25m),
                    KeyValuePair.Create("gpt-4.1|output", 5.75m),
                ])),
            new ManualRuntimeRateCardReadProvenance("https://example.com/prices", new DateOnly(2026, 11, 10)));

    private static HttpResponseMessage Response(string payload, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static string StrongTag(string payload) =>
        $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}\"";

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
