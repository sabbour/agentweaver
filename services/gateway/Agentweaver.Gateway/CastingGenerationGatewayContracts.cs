using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Gateway;

/// <summary>Common request fields for an accepted-run casting generation operation.</summary>
public interface IProjectCastingGenerationRequest
{
    long ExpectedConfigurationRevision { get; }
    string RunId { get; }
    string SessionId { get; }
    string IdempotencyKey { get; }
    string Prompt { get; }
    string? CastingUniverse { get; }
}

/// <summary>Strict request to formulate a casting proposal from a user brief.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FormulateProjectCastingProposalRequest : IProjectCastingGenerationRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Prompt { get; init; }
    public required string? CastingUniverse { get; init; }
}

/// <summary>Strict request to analyze project context and propose a casting.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalyzeProjectCastingProposalRequest : IProjectCastingGenerationRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Prompt { get; init; }
    public required string? CastingUniverse { get; init; }
}

/// <summary>Identifies the proposal and accepted run created by a generation request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CastingGenerationAcceptedResponse(
    Guid RequestId,
    Guid ProposalId,
    string RunId,
    string Mode,
    string State);

internal enum GatewayCastingGenerationMode
{
    Formulate,
    Analyze,
}

internal static class CastingGenerationGatewayContract
{
    internal static void Validate(IProjectCastingGenerationRequest request)
    {
        if (request.ExpectedConfigurationRevision <= 0 ||
            !IsValidIdentity(request.RunId) ||
            !IsValidIdentity(request.SessionId) ||
            !IsValidIdempotencyKey(request.IdempotencyKey) ||
            request.CastingUniverse is { Length: > 128 } ||
            request.CastingUniverse?.Any(char.IsControl) == true ||
            string.IsNullOrWhiteSpace(request.Prompt) ||
            request.Prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            request.Prompt.Contains('\0') ||
            request.Prompt.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("The casting generation request is invalid.");
    }

    private static bool IsValidIdentity(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    private static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
