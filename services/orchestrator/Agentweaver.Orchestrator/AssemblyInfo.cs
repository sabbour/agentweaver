// Exposes selected internals to the Orchestrator Core and Identity Broker test assemblies.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agentweaver.Orchestrator.Core.Tests")]
[assembly: InternalsVisibleTo("Agentweaver.Identity.Broker.Tests")]
