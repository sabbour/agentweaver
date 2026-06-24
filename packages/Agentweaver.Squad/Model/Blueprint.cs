namespace Agentweaver.Squad.Model;

/// <summary>
/// A team blueprint: a named, reusable starting point for a project that captures a roster of
/// catalog roles plus the default workflow, review policy, and sandbox profile to apply on creation.
/// Sources: predefined (embedded catalog resources), file (user-supplied JSON of this same shape),
/// and generated (produced by the model from a description). The JSON contract is snake_case.
/// </summary>
public sealed record Blueprint(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Roster,
    string Workflow,
    string ReviewPolicy,
    string SandboxProfile);
