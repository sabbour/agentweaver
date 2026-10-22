using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Projects.Config;

/// <summary>
/// Projects settings for collecting platform component reports from owner services. Each owner
/// address and the service token file are optional; an owner without settings is reported as
/// <c>unknown</c> with reason <c>ownerUnavailable</c>.
/// </summary>
/// <param name="TokenFile">Absolute path of the projected service token for the private audience, or null.</param>
/// <param name="Owners">HTTPS owner origins keyed by owner identifier.</param>
public sealed record PlatformComponentCollectorOptions(
    string? TokenFile,
    IReadOnlyDictionary<string, Uri> Owners)
{
    /// <summary>Configuration section that holds the collector settings.</summary>
    public const string SectionName = "PlatformComponents";

    /// <summary>Time limit for each owner call.</summary>
    public static TimeSpan OwnerTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Reads <c>PlatformComponents:ServiceToken:TokenFile</c> and
    /// <c>PlatformComponents:Owners:&lt;owner&gt;:BaseAddress</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is present but not valid.</exception>
    public static PlatformComponentCollectorOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var tokenFile = section["ServiceToken:TokenFile"];
        if (!string.IsNullOrWhiteSpace(tokenFile) && !Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException("PlatformComponents:ServiceToken:TokenFile must be an absolute path.");

        var owners = ImmutableDictionary.CreateBuilder<string, Uri>(StringComparer.Ordinal);
        foreach (var owner in PlatformComponentOwners.Reporting)
        {
            var value = section[$"Owners:{owner}:BaseAddress"];
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps ||
                address.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment))
                throw new InvalidOperationException(
                    $"PlatformComponents:Owners:{owner}:BaseAddress must be an HTTPS origin with no path.");
            owners[owner] = address;
        }

        return new(string.IsNullOrWhiteSpace(tokenFile) ? null : tokenFile, owners.ToImmutable());
    }
}

/// <summary>Reads one owner service's platform component report.</summary>
public interface IPlatformComponentOwnerClient
{
    /// <summary>Returns the owner's report, or throws when the owner can't answer.</summary>
    /// <param name="owner">Owner identifier from <see cref="PlatformComponentOwners"/>.</param>
    /// <param name="cancellationToken">Cancellation that includes the per-owner time limit.</param>
    Task<PlatformComponentOwnerReport> GetAsync(string owner, CancellationToken cancellationToken);
}

/// <summary>
/// Calls an owner's <c>GET /internal/platform/components</c> over HTTPS that trusts only the
/// service backend CA. It sends the projected service token, reads at most 64 KB, and checks
/// that the report names the expected owner.
/// </summary>
public sealed class HttpPlatformComponentOwnerClient : IPlatformComponentOwnerClient, IDisposable
{
    /// <summary>Owner route that returns the report.</summary>
    public const string OwnerRoute = "/internal/platform/components";

    private const int MaxResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    private readonly PlatformComponentCollectorOptions _options;
    private readonly Lazy<HttpClient> _client;

    /// <summary>Creates the client. The trusted HTTP handler is created on first use.</summary>
    public HttpPlatformComponentOwnerClient(PlatformComponentCollectorOptions options)
        : this(options, () => new HttpClient(RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        })
    {
    }

    /// <summary>Creates the client with a custom HTTP client factory, for tests.</summary>
    internal HttpPlatformComponentOwnerClient(PlatformComponentCollectorOptions options, Func<HttpClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientFactory);
        _options = options;
        _client = new Lazy<HttpClient>(clientFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The owner isn't configured or returned an invalid report.</exception>
    public async Task<PlatformComponentOwnerReport> GetAsync(string owner, CancellationToken cancellationToken)
    {
        if (!_options.Owners.TryGetValue(owner, out var address) || _options.TokenFile is null)
            throw new InvalidOperationException("The platform component owner isn't configured.");

        // Read the token on each call because the kubelet rotates projected tokens.
        var token = (await File.ReadAllTextAsync(_options.TokenFile, cancellationToken).ConfigureAwait(false)).Trim();
        if (token.Length == 0)
            throw new InvalidOperationException("The platform component service token is empty.");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, OwnerRoute));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.Value
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The platform component owner returned {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("The platform component owner response is too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bounded.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("The platform component owner response is too large.");
            bounded.Write(buffer, 0, read);
        }

        bounded.Position = 0;
        var report = await JsonSerializer.DeserializeAsync<PlatformComponentOwnerReport>(
            bounded, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (report is null || !string.Equals(report.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException("The platform component owner report is not valid.");
        return report;
    }

    /// <summary>Disposes the HTTP client when it was created.</summary>
    public void Dispose()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }
}

/// <summary>
/// Collects owner reports in parallel and combines them into the full platform component report.
/// Each owner gets <see cref="PlatformComponentCollectorOptions.OwnerTimeout"/>. A failed, slow,
/// or invalid owner makes its areas <c>unknown</c> with reason <c>ownerUnavailable</c>.
/// </summary>
public sealed class PlatformComponentCollector(
    IPlatformComponentOwnerClient ownerClient,
    TimeProvider timeProvider,
    ILogger<PlatformComponentCollector> logger)
{
    /// <summary>Time limit for each owner; tests can shorten it.</summary>
    internal TimeSpan OwnerTimeout { get; init; } = PlatformComponentCollectorOptions.OwnerTimeout;

    /// <summary>Returns a report that always contains every platform component area.</summary>
    /// <param name="cancellationToken">Caller cancellation; it is rethrown, not reported.</param>
    public async Task<PlatformComponentReport> CollectAsync(CancellationToken cancellationToken)
    {
        var reports = new ConcurrentDictionary<string, PlatformComponentOwnerReport?>(StringComparer.Ordinal);
        await Task.WhenAll(PlatformComponentOwners.Reporting.Select(async owner =>
            reports[owner] = await CollectOwnerAsync(owner, cancellationToken).ConfigureAwait(false)))
            .ConfigureAwait(false);
        return PlatformComponentReportBuilder.Compose(reports, timeProvider.GetUtcNow());
    }

    // Returns null for any owner failure other than caller cancellation.
    private async Task<PlatformComponentOwnerReport?> CollectOwnerAsync(string owner, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OwnerTimeout);
        try
        {
            return await ownerClient.GetAsync(owner, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Platform component owner {Owner} did not answer: {ErrorType}.", owner, exception.GetType().Name);
            return null;
        }
    }
}

/// <summary>Registers the platform component collector and its owner client.</summary>
public static class PlatformComponentCollectorServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PlatformComponentCollector"/> with options read from <paramref name="configuration"/>.
    /// Requires a registered <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddPlatformComponentCollector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(PlatformComponentCollectorOptions.Read(configuration));
        services.AddSingleton<IPlatformComponentOwnerClient, HttpPlatformComponentOwnerClient>();
        services.AddSingleton<PlatformComponentCollector>();
        return services;
    }
}