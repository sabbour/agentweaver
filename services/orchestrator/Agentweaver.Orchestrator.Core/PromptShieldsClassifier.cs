using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Supplies a bearer token for a Guardrails classifier without exposing the credential to the gate.</summary>
public interface IGuardrailsClassifierCredential
{
    /// <summary>Returns a short-lived bearer access token for the classifier endpoint.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Typed, versioned options for the Azure AI Content Safety Prompt Shields classifier.
/// The options contain an endpoint and limits only. They never contain a key or a token.
/// </summary>
public sealed record PromptShieldsClassifierOptions(
    string ProviderId,
    string OptionsRevision,
    Uri Endpoint,
    string ApiVersion,
    TimeSpan RequestTimeout)
    : GuardrailsProviderOptions(
        ProviderId,
        PromptShieldsClassifier.ContractAdapterVersion,
        PromptShieldsClassifier.ContractOptionsSchemaVersion,
        OptionsRevision,
        RequestTimeout)
{
    /// <summary>Validates the shared identity and timeout, an HTTPS endpoint, and a supported API version.</summary>
    public override void Validate()
    {
        base.Validate();
        // The endpoint must be a plain absolute HTTPS root. Credentials in the user-info part, or a query or
        // fragment, would leak into or alter the request URI that the classifier builds, so they are rejected.
        // Only API versions whose response shape this classifier parses are accepted.
        if (Endpoint is null ||
            !Endpoint.IsAbsoluteUri ||
            Endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) ||
            !string.IsNullOrEmpty(Endpoint.Fragment) ||
            !PromptShieldsClassifier.SupportedApiVersions.Contains(ApiVersion))
            throw new ArgumentException("Prompt Shields options are invalid.");
    }
}

/// <summary>
/// Calls the Azure AI Content Safety Prompt Shields API for the two directions it supports:
/// user input text (<c>userPrompt</c>) and tool result text (<c>documents</c>).
/// Every other stage or content class returns a typed failure.
/// </summary>
/// <remarks>
/// Content longer than one request allows is split into sequential requests on character boundaries.
/// Any detected attack in any request produces a finding. Transport errors, throttling, and server
/// errors return <see cref="GuardrailsClassifierFailure.Unavailable"/>; malformed responses return
/// <see cref="GuardrailsClassifierFailure.InvalidResponse"/>. The classifier never logs or returns content.
/// </remarks>
public sealed class PromptShieldsClassifier : IGuardrailsClassifier<PromptShieldsClassifierOptions>
{
    public const string DefaultProviderId = "azure-prompt-shields";
    public const string UserPromptAttackFinding = "prompt_shields.user_prompt_attack";
    public const string DocumentAttackFinding = "prompt_shields.document_attack";
    public const int MaximumCharactersPerRequest = 10_000;
    public const int MaximumResponseBytes = 65_536;
    public static readonly Version ContractAdapterVersion = new(1, 0, 0);
    public const int ContractOptionsSchemaVersion = 1;
    public static readonly ImmutableHashSet<string> SupportedApiVersions =
        ImmutableHashSet.Create(StringComparer.Ordinal, "2024-09-01");

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly IGuardrailsClassifierCredential _credential;

    /// <summary>Creates a classifier that uses the given HTTP client and bearer token source.</summary>
    public PromptShieldsClassifier(
        HttpClient http,
        IGuardrailsClassifierCredential credential,
        string providerId = DefaultProviderId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        // The provider ID is matched against catalog entries and options, so it must be a well-formed identifier.
        if (!AgtPolicyProvider.IsIdentifier(providerId))
            throw new ArgumentException("The Prompt Shields provider ID is invalid.", nameof(providerId));
        ProviderId = providerId;
    }

    public string ProviderId { get; }
    public Version AdapterVersion => ContractAdapterVersion;
    public int OptionsSchemaVersion => ContractOptionsSchemaVersion;
    public ImmutableHashSet<string> Capabilities => GuardrailsProviderCapabilities.PromptShieldsSupported;

    /// <summary>Classifies user input or tool result text and returns clear, findings, or a typed failure.</summary>
    public async Task<GuardrailsClassifierResult> ClassifyAsync(
        PromptShieldsClassifierOptions options,
        GuardrailsClassifierRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        // Bad options are a configuration problem, not a transient outage. Return a typed failure instead of
        // throwing so the gate can fail closed with a stable reason.
        try
        {
            options.Validate();
        }
        catch (ArgumentException)
        {
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);
        }
        // Reject options meant for another provider, and any stage or content class that Prompt Shields can't
        // analyze. Empty content is also rejected: "clear" must mean the service actually checked something.
        if (options.ProviderId != ProviderId || request.ContentClass != GuardrailsContentClass.Text ||
            request.Stage is not (GuardrailsContentStage.UserInput or GuardrailsContentStage.ToolResult) ||
            request.CanonicalContent.IsDefaultOrEmpty)
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);

        // Decode strictly. A lenient decoder would replace invalid bytes, so the service would classify text
        // that differs from the bytes the gate is admitting.
        string text;
        try
        {
            text = StrictUtf8.GetString(request.CanonicalContent.AsSpan());
        }
        catch (DecoderFallbackException)
        {
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);
        }

        // User input goes in the userPrompt field; tool results go in the documents field.
        var userPrompt = request.Stage == GuardrailsContentStage.UserInput;
        var detected = false;
        // Every chunk is checked, even after an attack is found, because one failed chunk must still fail the
        // whole request. A partial check can never be reported as clear.
        foreach (var chunk in Split(text))
        {
            var result = await ClassifyChunkAsync(options, chunk, userPrompt, cancellationToken)
                .ConfigureAwait(false);
            if (result.Failure is not null)
                return result.Failure;
            detected |= result.Detected;
        }

        // Report one finding for the whole content. The finding never includes the text or the chunk position.
        return detected
            ? GuardrailsClassifierResult.Detected(
            [
                new GuardrailsFinding(
                    userPrompt ? UserPromptAttackFinding : DocumentAttackFinding,
                    GuardrailsFindingSeverity.High)
            ])
            : GuardrailsClassifierResult.Clear();
    }

    private async Task<(bool Detected, GuardrailsClassifierResult? Failure)> ClassifyChunkAsync(
        PromptShieldsClassifierOptions options,
        string chunk,
        bool userPrompt,
        CancellationToken cancellationToken)
    {
        // Get a fresh token for each chunk so a long multi-chunk request doesn't reuse an expired token.
        // Any credential error counts as "unavailable". Cancellation and out-of-memory still propagate,
        // because they are not classifier outages.
        string token;
        try
        {
            token = await _credential.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.Unavailable));
        }
        if (string.IsNullOrWhiteSpace(token))
            return (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.Unavailable));

        // The API expects both fields. Only the field for the current direction carries the chunk.
        var body = userPrompt
            ? JsonSerializer.SerializeToUtf8Bytes(new { userPrompt = chunk, documents = Array.Empty<string>() })
            : JsonSerializer.SerializeToUtf8Bytes(new { userPrompt = string.Empty, documents = new[] { chunk } });
        using var message = new HttpRequestMessage(HttpMethod.Post, RequestUri(options))
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Read headers first so the body can be read with a size limit below.
        // Only transport errors are caught. Cancellation, including an HttpClient timeout, propagates to the caller.
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.Unavailable));
        }

        using (response)
        {
            // 429, 408, and 5xx are transient, so they map to "unavailable". Other errors (for example 400,
            // 401, 403, or 404) mean the request or configuration is wrong, so they map to "provider failure".
            if (!response.IsSuccessStatusCode)
                return (false, GuardrailsClassifierResult.Failed(
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                    (int)response.StatusCode >= 500
                        ? GuardrailsClassifierFailure.Unavailable
                        : GuardrailsClassifierFailure.ProviderFailure));

            // A connection that drops mid-body is transient. A body over the size limit is treated as a
            // malformed response, because a valid Prompt Shields response is always small.
            byte[] payload;
            try
            {
                payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.Unavailable));
            }
            catch (InvalidDataException)
            {
                return (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.InvalidResponse));
            }

            // A missing or wrongly typed attackDetected flag fails closed as an invalid response; it is never
            // read as "no attack".
            var detected = ParseAttackDetected(payload, userPrompt);
            return detected is null
                ? (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.InvalidResponse))
                : (detected.Value, null);
        }
    }

    private static Uri RequestUri(PromptShieldsClassifierOptions options)
    {
        // Trim the trailing slash so an endpoint with or without one produces the same path.
        var root = options.Endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{root}/contentsafety/text:shieldPrompt?api-version={Uri.EscapeDataString(options.ApiVersion)}");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // Reject early when the declared length is already too large.
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The Prompt Shields response is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        // Also count the bytes actually read, because the length header can be missing or wrong.
        // This keeps a faulty or hostile server from making the Orchestrator buffer an unbounded body.
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The Prompt Shields response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Returns null when the response does not contain the analysis for the direction that was sent.
    private static bool? ParseAttackDetected(byte[] payload, bool userPrompt)
    {
        try
        {
            // The expected response is shallow, so a low depth limit is enough and blocks deeply nested input.
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            // For user input, read userPromptAnalysis.attackDetected. It must be a real JSON boolean.
            if (userPrompt)
                return root.TryGetProperty("userPromptAnalysis", out var analysis) &&
                    analysis.ValueKind == JsonValueKind.Object &&
                    analysis.TryGetProperty("attackDetected", out var flag) &&
                    flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? flag.GetBoolean()
                        : null;

            // For tool results, exactly one document was sent, so exactly one analysis must come back.
            // Any other count means the response doesn't match the request.
            if (!root.TryGetProperty("documentsAnalysis", out var documents) ||
                documents.ValueKind != JsonValueKind.Array ||
                documents.GetArrayLength() != 1)
                return null;
            var item = documents[0];
            return item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("attackDetected", out var documentFlag) &&
                documentFlag.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? documentFlag.GetBoolean()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Splits text into request-sized chunks without separating a surrogate pair.
    internal static IEnumerable<string> Split(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(MaximumCharactersPerRequest, text.Length - start);
            // If the chunk would end on the first half of a surrogate pair, end one character earlier so the
            // pair stays together in the next chunk. Each chunk then stays valid text for the service.
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }
}
