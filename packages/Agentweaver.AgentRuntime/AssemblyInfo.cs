using System.Runtime.CompilerServices;

// The broker tests verify internal grant and receipt behavior without exposing those APIs publicly.
[assembly: InternalsVisibleTo("Agentweaver.Identity.Broker.Tests")]
