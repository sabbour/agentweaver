namespace Scaffolder.Domain;

/// <summary>
/// Terminal and intermediate run states. Every run MUST end in a visible
/// terminal state (Principle X).
/// </summary>
public enum RunStatus
{
    Pending,      // submitted, not yet started
    InProgress,   // agent loop running
    Completed,    // loop ended normally, awaiting review
    Failed,       // terminal failure (provider failure, content safety, etc.)
    Bounded,      // hit step or time limit
    Reviewing,    // human reviewing diff
    Approved,     // human approved, merge in progress or done
    Declined      // human declined
}
