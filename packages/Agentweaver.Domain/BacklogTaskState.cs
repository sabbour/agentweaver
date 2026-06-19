namespace Agentweaver.Domain;

public enum BacklogTaskState
{
    Backlog,   // captured-but-not-committed
    Ready,     // committed, awaiting coordinator pickup (claim gate open while run_id IS NULL)
    Claimed,   // claimed by a heartbeat; RunId set to a persisted coordinator run; card renders from run state
}
