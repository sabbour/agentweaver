using Microsoft.Extensions.AI;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Providers;

/// <summary>
/// Creates a configured <see cref="IChatClient"/> for a run's model source.
/// One implementation exists per permitted provider (Principle II).
/// </summary>
public interface IChatClientFactory
{
    IChatClient CreateForRun(Run run);
}
