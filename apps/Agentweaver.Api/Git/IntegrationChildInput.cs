namespace Agentweaver.Api.Git;

/// <summary>A child commit captured when its branch tip matched the recorded output tree.</summary>
public sealed record IntegrationChildInput(string Branch, string CommitSha, string? RevisionBaseCommitSha = null);
