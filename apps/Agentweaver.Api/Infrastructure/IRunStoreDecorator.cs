namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Implemented by <see cref="IRunStore"/> decorators so a caller that needs a specific store in the
/// chain can find it. <see cref="RunStreamStore"/> and <see cref="SqliteRunEventStream"/> need the
/// <see cref="RunActiveClaimGuardedRunStore"/> to take its lifecycle claim for a conditional append,
/// and that store is no longer guaranteed to be the outermost one.
/// </summary>
public interface IRunStoreDecorator
{
    /// <summary>The store this decorator wraps.</summary>
    IRunStore Inner { get; }
}

/// <summary>Walks an <see cref="IRunStore"/> decorator chain.</summary>
public static class RunStoreChain
{
    /// <summary>
    /// Returns the first store in the chain assignable to <typeparamref name="T"/>, or
    /// <see langword="null"/> when no link matches.
    /// </summary>
    public static T? Find<T>(IRunStore? store) where T : class
    {
        while (store is not null)
        {
            if (store is T match)
                return match;
            store = (store as IRunStoreDecorator)?.Inner;
        }
        return null;
    }
}
