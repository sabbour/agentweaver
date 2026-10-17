using System.Security.Claims;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions;

/// <summary>Reads the authenticated actor and exact project/run scope used by Events &amp; Sessions.</summary>
public static class SessionIdentityClaims
{
    public const string Subject = "sub";
    public const string ProjectId = "project_id";
    public const string RunId = "run_id";

    /// <summary>Returns the unique authenticated project/run scope, or false for missing or duplicate claims.</summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="scope">The validated project/run scope when successful.</param>
    /// <returns>True only when the caller has one valid subject, project, and run claim.</returns>
    public static bool TryGetScope(ClaimsPrincipal principal, out SessionRunScope? scope) =>
        TryGetScope(principal, out scope, out _);

    /// <summary>Returns the validated project/run scope and actor subject from one unambiguous identity.</summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="scope">The validated project/run scope when successful.</param>
    /// <param name="actorId">The subject claim when successful.</param>
    /// <returns>False when authentication or any required claim is missing, repeated, or invalid.</returns>
    public static bool TryGetScope(
        ClaimsPrincipal principal,
        out SessionRunScope? scope,
        out string? actorId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        scope = null;
        actorId = null;
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
            actorId = subjects[0].Value;
            scope = new SessionRunScope(projects[0].Value, runs[0].Value);
            return true;
        }
        catch (ArgumentException)
        {
            actorId = null;
            return false;
        }
    }
}

public readonly record struct SessionRunScope
{
    public string ProjectId { get; }
    public string RunId { get; }

    /// <summary>Validates and stores a project/run pair using the same identifier rules as a session identity.</summary>
    public SessionRunScope(string projectId, string runId)
    {
        var identity = new SessionIdentity(projectId, runId, "_");
        ProjectId = identity.ProjectId;
        RunId = identity.RunId;
    }

    /// <summary>Creates a complete session identity within this project/run scope.</summary>
    public SessionIdentity ForSession(string sessionId) => new(ProjectId, RunId, sessionId);
}

/// <summary>Signals that the caller lacks one valid project/run identity.</summary>
public sealed class SessionAuthenticationException()
    : Exception("Exactly one valid project_id and run_id claim are required.");
