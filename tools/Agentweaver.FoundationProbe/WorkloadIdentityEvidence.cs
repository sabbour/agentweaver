using System.Text;
using System.Text.Json;

namespace Agentweaver.FoundationProbe;

internal static class WorkloadIdentityEvidence
{
    private const string ExpectedSubject = "system:serviceaccount:agentweaver-v1-p0:foundation-probe";
    private const string ExpectedAudience = "api://AzureADTokenExchange";

    public static async Task<ProbeIdentityEvidence> ReadAndValidateAsync(
        ProbeTarget target,
        string tokenFilePath,
        string? clientId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenFilePath) || !Path.IsPathFullyQualified(tokenFilePath) ||
            !string.Equals(clientId, target.FoundationProbeIdentity.ClientId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tenantId, target.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("workload_identity_configuration_mismatch");

        string token;
        try
        {
            token = await File.ReadAllTextAsync(tokenFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProbeException("projected_token_unavailable", exception);
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new ProbeException("projected_token_invalid");

        byte[] payload;
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            payload = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new ProbeException("projected_token_invalid", exception);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var issuer = root.TryGetProperty("iss", out var issuerElement) ? issuerElement.GetString() : null;
            var subject = root.TryGetProperty("sub", out var subjectElement) ? subjectElement.GetString() : null;
            var audiences = ReadAudiences(root);
            if (issuer != target.AksOidcIssuerUrl || subject != ExpectedSubject ||
                audiences.Count != 1 || audiences[0] != ExpectedAudience)
                throw new ProbeException("workload_identity_claim_mismatch");
            cancellationToken.ThrowIfCancellationRequested();
            return new ProbeIdentityEvidence(issuer, subject, audiences[0]);
        }
        catch (JsonException exception)
        {
            throw new ProbeException("projected_token_invalid", exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static List<string> ReadAudiences(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var audience))
            throw new ProbeException("workload_identity_claim_mismatch");
        if (audience.ValueKind == JsonValueKind.String)
            return [audience.GetString() ?? ""];
        if (audience.ValueKind == JsonValueKind.Array)
            return audience.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? "")
                .ToList();
        throw new ProbeException("workload_identity_claim_mismatch");
    }
}
