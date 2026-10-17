namespace Agentweaver.Persistence.Postgres;

/// <summary>Reports whether a consumer/message pair was recorded for the first time.</summary>
public enum InboxAdmission
{
    /// <summary>The receipt was inserted and the message may be processed in this transaction.</summary>
    Admitted,

    /// <summary>The receipt already existed, so the message is a duplicate for this consumer.</summary>
    Duplicate
}
