namespace Agentweaver.Knowledge;

public sealed class KnowledgeApiException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class KnowledgeStorageUnavailableException()
    : Exception("Knowledge PostgreSQL storage is unavailable.");

public sealed class KnowledgeProviderUnavailableException(string message)
    : Exception(message);

public sealed class KnowledgeContextCandidateLimitException(int maximumCandidates)
    : Exception($"Context retrieval exceeds the configured limit of {maximumCandidates} records.")
{
    public int MaximumCandidates { get; } = maximumCandidates;
}

public sealed class MandatoryContextBudgetExceededException(int budgetCharacters, int requiredCharacters)
    : Exception(
        $"Active approved decisions require {requiredCharacters} characters, exceeding the structured context budget of {budgetCharacters}.")
{
    public int BudgetCharacters { get; } = budgetCharacters;
    public int RequiredCharacters { get; } = requiredCharacters;
}
