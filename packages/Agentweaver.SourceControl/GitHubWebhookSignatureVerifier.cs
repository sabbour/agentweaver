using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed record GitHubWebhookEnvelope(
    string DeliveryId,
    string EventName,
    string Action,
    SourceControlRepositoryIdentity Repository,
    long ProviderRepositoryId,
    long? PullRequestNumber,
    string? HeadSha,
    string? BaseSha);

public static class GitHubWebhookSignatureVerifier
{
    public static bool VerifyRawBody(
        ReadOnlySpan<byte> rawBody,
        string? signatureHeader,
        SecretCredential webhookSecret)
    {
        ArgumentNullException.ThrowIfNull(webhookSecret);
        try
        {
            if (signatureHeader is null ||
                !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal) ||
                signatureHeader.Length != 7 + 64)
                return false;

            var signatureText = signatureHeader.AsSpan(7);
            foreach (var character in signatureText)
            {
                if (!Uri.IsHexDigit(character))
                    return false;
            }

            var suppliedSignature = Convert.FromHexString(signatureText);
            var secretBytes = Encoding.UTF8.GetBytes(webhookSecret.GetValue());
            var expectedSignature = HMACSHA256.HashData(secretBytes, rawBody);
            return CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature);
        }
        finally
        {
            webhookSecret.Invalidate();
        }
    }

    public static GitHubWebhookEnvelope ParseVerifiedPayload(
        ReadOnlySpan<byte> rawBody,
        string? deliveryId,
        string? eventName,
        SourceControlRepositoryIdentity expectedRepository,
        long expectedProviderRepositoryId)
    {
        ArgumentNullException.ThrowIfNull(expectedRepository);
        if (!Guid.TryParse(deliveryId, out _) ||
            string.IsNullOrWhiteSpace(eventName) ||
            expectedProviderRepositoryId <= 0)
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The GitHub webhook delivery or expected repository binding is invalid.");

        try
        {
            using var document = JsonDocument.Parse(rawBody.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("repository", out var repositoryElement) ||
                repositoryElement.ValueKind != JsonValueKind.Object)
                throw InvalidWebhookPayload();
            var identity = ParseRepositoryIdentity(ReadRequiredString(repositoryElement, "full_name"));
            var repositoryId = ReadPositiveInt64(repositoryElement, "id");
            if (!string.Equals(identity.Owner, expectedRepository.Owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity.Name, expectedRepository.Name, StringComparison.OrdinalIgnoreCase) ||
                repositoryId != expectedProviderRepositoryId)
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidBinding,
                    "The webhook payload does not match the pinned GitHub repository.");

            var action = root.TryGetProperty("action", out var actionElement) &&
                         actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()!
                : string.Empty;
            long? pullRequestNumber = null;
            string? headSha = null;
            string? baseSha = null;
            if (root.TryGetProperty("pull_request", out var pullRequest) &&
                pullRequest.ValueKind == JsonValueKind.Object)
            {
                pullRequestNumber = ReadPositiveInt64(pullRequest, "number");
                headSha = ReadNestedRequiredString(pullRequest, "head", "sha");
                baseSha = ReadNestedRequiredString(pullRequest, "base", "sha");
                if (!IsGitSha(headSha) || !IsGitSha(baseSha))
                    throw InvalidWebhookPayload();
            }

            return new GitHubWebhookEnvelope(
                deliveryId!,
                eventName,
                action,
                identity,
                repositoryId,
                pullRequestNumber,
                headSha,
                baseSha);
        }
        catch (JsonException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub sent malformed webhook JSON.",
                innerException: exception);
        }
    }

    private static SourceControlRepositoryIdentity ParseRepositoryIdentity(string fullName)
    {
        var parts = fullName.Split('/');
        if (parts.Length != 2)
            throw InvalidWebhookPayload();
        try
        {
            return new SourceControlRepositoryIdentity(parts[0], parts[1]);
        }
        catch (ArgumentException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub sent an invalid webhook repository identity.",
                innerException: exception);
        }
    }

    private static string ReadRequiredString(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw InvalidWebhookPayload();
    }

    private static string ReadNestedRequiredString(
        JsonElement source,
        string parent,
        string property)
    {
        if (!source.TryGetProperty(parent, out var parentValue) ||
            parentValue.ValueKind != JsonValueKind.Object)
            throw InvalidWebhookPayload();
        return ReadRequiredString(parentValue, property);
    }

    private static long ReadPositiveInt64(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) &&
            value.TryGetInt64(out var number) &&
            number > 0)
            return number;
        throw InvalidWebhookPayload();
    }

    private static bool IsGitSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static SourceControlOperationException InvalidWebhookPayload() =>
        new(SourceControlFailureCode.InvalidResponse, "The GitHub webhook payload is invalid.");
}
