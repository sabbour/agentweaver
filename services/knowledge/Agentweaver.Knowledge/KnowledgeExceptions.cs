namespace Agentweaver.Knowledge;

/// <summary>An expected API rejection with the stable error code and HTTP status returned to callers.</summary>
public sealed class KnowledgeApiException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Signals that a Knowledge storage operation could not be completed or trusted.</summary>
public sealed class KnowledgeStorageUnavailableException()
    : Exception("Knowledge storage is unavailable.");

/// <summary>Signals that the selected Memory provider cannot serve this request.</summary>
public sealed class KnowledgeProviderUnavailableException(string message)
    : Exception(message);

/// <summary>Signals that context retrieval found more records than the configured scan bound.</summary>
public sealed class KnowledgeContextCandidateLimitException(int maximumCandidates)
    : Exception($"Context retrieval exceeds the configured limit of {maximumCandidates} records.")
{
    public int MaximumCandidates { get; } = maximumCandidates;
}

/// <summary>Signals that required approved Decisions alone exceed the structured context budget.</summary>
public sealed class MandatoryContextBudgetExceededException(int budgetCharacters, int requiredCharacters)
    : Exception(
        $"Active approved decisions require {requiredCharacters} characters, exceeding the structured context budget of {budgetCharacters}.")
{
    public int BudgetCharacters { get; } = budgetCharacters;
    public int RequiredCharacters { get; } = requiredCharacters;
}
