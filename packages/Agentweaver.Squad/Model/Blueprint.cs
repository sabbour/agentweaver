namespace Agentweaver.Squad.Model;

/// <summary>
/// A team blueprint: a named, reusable starting point for a project that captures a roster of
/// catalog roles plus a set of workflows, review policy, and sandbox profile to apply on creation.
/// A Blueprint now bundles one or more functional workflow ids; the first in the set is the default.
/// Sources: predefined (embedded catalog resources), file (user-supplied JSON of this same shape),
/// and generated (produced by the model from a description). The JSON contract is snake_case.
/// </summary>
public sealed record Blueprint(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Roster,
    IReadOnlyList<string> Workflows,
    string ReviewPolicy,
    string SandboxProfile)
{
    /// <summary>
    /// The default workflow id for this blueprint — the first entry in <see cref="Workflows"/>.
    /// Falls back to <c>"default"</c> when the set is empty. Used by the project store and
    /// backward-compatible API surfaces that reference a single workflow id.
    /// </summary>
    public string Workflow => Workflows.Count > 0 ? Workflows[0] : "default";
}
