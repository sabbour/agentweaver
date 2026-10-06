using System.Security.Claims;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions;

public static class SessionIdentityClaims
{
    public const string Subject = "sub";
    public const string ProjectId = "project_id";
    public const string RunId = "run_id";

    public static bool TryGetScope(ClaimsPrincipal principal, out SessionRunScope? scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        scope = null;
        // Identity Broker emits a GUID sub and adds the owner pair only for an active core run grant.
        var subjects = principal.FindAll(Subject).Take(2).ToArray();
        var projects = principal.FindAll(ProjectId).Take(2).ToArray();
        var runs = principal.FindAll(RunId).Take(2).ToArray();
        if (!principal.Identities.Any(identity => identity.IsAuthenticated) ||
            subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out _) ||
            projects.Length != 1 || runs.Length != 1)
            return false;

        try
        {
            scope = new SessionRunScope(projects[0].Value, runs[0].Value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public readonly record struct SessionRunScope
{
    public string ProjectId { get; }
    public string RunId { get; }

    public SessionRunScope(string projectId, string runId)
    {
        var identity = new SessionIdentity(projectId, runId, "_");
        ProjectId = identity.ProjectId;
        RunId = identity.RunId;
    }

    public SessionIdentity ForSession(string sessionId) => new(ProjectId, RunId, sessionId);
}

public sealed class SessionAuthenticationException()
    : Exception("Exactly one valid project_id and run_id claim are required.");
