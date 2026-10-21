using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Orchestrator;

/// <summary>Binds one interactive run that the caller started to the caller's private Chief of Staff list.</summary>
/// <param name="ProjectId">The project that owns the run.</param>
/// <param name="RunId">A run created through <c>POST /api/projects/{projectId}/runs</c> by the same caller.</param>
public sealed record BindChiefOfStaffConversationRequest(string ProjectId, string RunId);

/// <summary>One private Chief of Staff conversation entry.</summary>
/// <remarks>
/// When <see cref="Access"/> is <c>unavailable</c>, the caller can no longer read the project, and every
/// project-owned field is null. The entry stays in the list so that the caller can see that access ended.
/// </remarks>
/// <param name="ConversationId">The opaque conversation identifier.</param>
/// <param name="Access"><c>available</c> or <c>unavailable</c>.</param>
/// <param name="ProjectId">The project, or null when unavailable.</param>
/// <param name="RunId">The bound run, or null when unavailable.</param>
/// <param name="RootSessionId">The root session that holds the message journal, or null when unavailable.</param>
/// <param name="Title">The run title, or null when unavailable.</param>
/// <param name="RequestState">The run request state: <c>awaiting_binding</c>, <c>started</c>, or <c>cancelled</c>.</param>
/// <param name="RunState">The derived run state from the run list, or null before the run is accepted.</param>
/// <param name="CreatedAt">When the conversation was bound.</param>
public sealed record ChiefOfStaffConversation(
    string ConversationId,
    string Access,
    string? ProjectId,
    string? RunId,
    string? RootSessionId,
    string? Title,
    string? RequestState,
    string? RunState,
    DateTimeOffset CreatedAt);

/// <summary>One newest-first page of the caller's conversations.</summary>
public sealed record ChiefOfStaffConversationPage(
    ImmutableArray<ChiefOfStaffConversation> Items,
    string? NextCursor);

/// <summary>One manual checklist item on a Priority.</summary>
/// <param name="ItemId">A stable item identifier. The owner assigns one when the request omits it.</param>
/// <param name="Text">The item text, 1–500 characters.</param>
/// <param name="Done">Whether the user checked the item.</param>
public sealed record ChiefOfStaffChecklistItem(string? ItemId, string Text, bool Done);

/// <summary>Creates a Priority at the end of the caller's list.</summary>
/// <param name="Outcome">The outcome, 1–200 characters.</param>
/// <param name="Notes">Optional notes, up to 16 KiB of UTF-8.</param>
/// <param name="Checklist">Optional checklist, up to 50 items.</param>
public sealed record CreateChiefOfStaffPriorityRequest(
    string Outcome,
    string? Notes,
    ImmutableArray<ChiefOfStaffChecklistItem>? Checklist);

/// <summary>Changes Priority content at an expected revision. Omitted fields keep their values.</summary>
/// <remarks>An empty <see cref="Notes"/> value clears the notes. <see cref="Checklist"/> replaces the whole list.</remarks>
public sealed record UpdateChiefOfStaffPriorityRequest(
    long ExpectedRevision,
    string? Outcome,
    string? Notes,
    string? State,
    ImmutableArray<ChiefOfStaffChecklistItem>? Checklist);

/// <summary>Replaces the order of every Priority in the caller's list at an expected order revision.</summary>
public sealed record ReorderChiefOfStaffPrioritiesRequest(
    long ExpectedOrderRevision,
    ImmutableArray<string> PriorityIds);

/// <summary>Adds one typed link to a Priority at an expected revision.</summary>
/// <remarks>
/// <c>run</c> needs <see cref="RunId"/>. <c>conversation</c> needs <see cref="ConversationId"/>.
/// <c>issue</c> and <c>pull_request</c> need <see cref="Number"/>, <see cref="ProviderId"/> and
/// <see cref="Repository"/>. Older number-only links remain readable but have no repository identity.
/// <c>artifact</c> needs <see cref="RunId"/>
/// and <see cref="ArtifactId"/>. Every link needs <see cref="ProjectId"/>.
/// </remarks>
public sealed record AddChiefOfStaffPriorityLinkRequest(
    long ExpectedRevision,
    string Kind,
    string ProjectId,
    string? RunId,
    string? ConversationId,
    long? Number,
    string? ArtifactId,
    string? ProviderId = null,
    ProjectSourceControlRepository? Repository = null);

/// <summary>A typed link from a Priority to an owner record, resolved against current access.</summary>
/// <remarks>
/// <see cref="Access"/> is <c>available</c>, <c>missing</c> (readable project, target not found), or
/// <c>unavailable</c> (the caller can no longer read the project). An unavailable link carries only
/// <see cref="LinkId"/>, <see cref="Kind"/>, and <see cref="Access"/>.
/// <see cref="State"/> is the current run state for run and conversation links, and <c>unavailable</c> for
/// issue, pull request, and artifact links, because the owner has no current-state read for them.
/// </remarks>
public sealed record ChiefOfStaffPriorityLink(
    string LinkId,
    string Kind,
    string Access,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConversationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Number,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectSourceControlRepository? Repository = null);

/// <summary>One Priority in the caller's list.</summary>
/// <remarks>Outcome, notes, and checklist are the caller's own content and are always returned.</remarks>
public sealed record ChiefOfStaffPriority(
    string PriorityId,
    string Outcome,
    string? Notes,
    string State,
    ImmutableArray<ChiefOfStaffChecklistItem> Checklist,
    int Position,
    long Revision,
    ImmutableArray<ChiefOfStaffPriorityLink> Links,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The caller's ordered Priorities and the revision that guards their order.</summary>
public sealed record ChiefOfStaffPriorityList(
    long OrderRevision,
    ImmutableArray<ChiefOfStaffPriority> Items);
