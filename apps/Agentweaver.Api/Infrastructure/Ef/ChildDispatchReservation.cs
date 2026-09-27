namespace Agentweaver.Api.Infrastructure.Ef;

public enum ChildDispatchReservationState
{
    Claimed,
    NotOwner,
    LeaseHeld,
    ExistingActive,
}

/// <summary>Only Claimed authorizes launch; the token fences this child execution.</summary>
public sealed record ChildDispatchReservation(
    ChildDispatchReservationState State,
    string? ChildRunId = null,
    long FencingToken = 0,
    int LifecycleGeneration = 0,
    string? ExistingStatus = null);
