// Exposes internal broker implementation to its dedicated test assembly without making those APIs public.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agentweaver.Identity.Broker.Tests")]
