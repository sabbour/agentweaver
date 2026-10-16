using System.Runtime.CompilerServices;

// Lets the focused acceptance tests exercise internal probe operations without widening the production API.
[assembly: InternalsVisibleTo("Agentweaver.FoundationProbe.Tests")]
