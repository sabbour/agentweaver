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
        try
        {
            options.Validate();
        }
        catch (ArgumentException)
        {
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);
        }
        if (options.ProviderId != ProviderId || request.ContentClass != GuardrailsContentClass.Text ||
            request.Stage is not (GuardrailsContentStage.UserInput or GuardrailsContentStage.ToolResult) ||
            request.CanonicalContent.IsDefaultOrEmpty)
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);

        string text;
        try
        {
            text = StrictUtf8.GetString(request.CanonicalContent.AsSpan());
        }
        catch (DecoderFallbackException)
        {
            return GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.ProviderFailure);
        }

        var userPrompt = request.Stage == GuardrailsContentStage.UserInput;
        var detected = false;
        foreach (var chunk in Split(text))
        {
            var result = await ClassifyChunkAsync(options, chunk, userPrompt, cancellationToken)
                .ConfigureAwait(false);
            if (result.Failure is not null)
                return result.Failure;
            detected |= result.Detected;
        }

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
            if (!response.IsSuccessStatusCode)
                return (false, GuardrailsClassifierResult.Failed(
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                    (int)response.StatusCode >= 500
                        ? GuardrailsClassifierFailure.Unavailable
                        : GuardrailsClassifierFailure.ProviderFailure));

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

            var detected = ParseAttackDetected(payload, userPrompt);
            return detected is null
                ? (false, GuardrailsClassifierResult.Failed(GuardrailsClassifierFailure.InvalidResponse))
                : (detected.Value, null);
        }
    }

    private static Uri RequestUri(PromptShieldsClassifierOptions options)
    {
        var root = options.Endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{root}/contentsafety/text:shieldPrompt?api-version={Uri.EscapeDataString(options.ApiVersion)}");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The Prompt Shields response is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
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
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (userPrompt)
                return root.TryGetProperty("userPromptAnalysis", out var analysis) &&
                    analysis.ValueKind == JsonValueKind.Object &&
                    analysis.TryGetProperty("attackDetected", out var flag) &&
                    flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? flag.GetBoolean()
                        : null;

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
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }
}
