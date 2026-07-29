using FluentAssertions;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Unit tests for the optional workflow <c>trigger:</c> block (issue #53): schedule + event triggers
/// parse into a <see cref="WorkflowTrigger"/>, malformed/unsupported cadences are rejected at load
/// time with a clear message, and a workflow with NO trigger key continues to load with a null
/// trigger (full backward compatibility with every pre-existing workflow file).
/// </summary>
public sealed class WorkflowTriggerLoaderTests
{
    private const string BaseYaml = """
        id: triage
        name: Triage
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Do the work."
        edges: []
        """;

    [Fact]
    public void Load_WorkflowWithoutTrigger_LeavesTriggerNull()
    {
        var result = WorkflowDefinitionLoader.Load(BaseYaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        result.Definition!.Trigger.Should().BeNull();
    }

    [Fact]
    public void Load_WeeklyScheduleTrigger_Parses()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: weekly
              day_of_week: monday
              time_of_day: "09:00"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var trigger = result.Definition!.Trigger;
        trigger.Should().NotBeNull();
        trigger!.Type.Should().Be(WorkflowTriggerType.Schedule);
        trigger.Interval.Should().Be(WorkflowScheduleInterval.Weekly);
        trigger.DayOfWeek.Should().Be(DayOfWeek.Monday);
        trigger.TimeOfDay.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public void Load_DailyScheduleTrigger_Parses_NoDayOfWeekRequired()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: daily
              time_of_day: "06:30"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var trigger = result.Definition!.Trigger;
        trigger!.Interval.Should().Be(WorkflowScheduleInterval.Daily);
        trigger.DayOfWeek.Should().BeNull();
        trigger.TimeOfDay.Should().Be(new TimeOnly(6, 30));
    }

    [Fact]
    public void Load_MonthlyScheduleTrigger_Parses()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: monthly
              day_of_month: 1
              time_of_day: "08:00"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var trigger = result.Definition!.Trigger;
        trigger!.Interval.Should().Be(WorkflowScheduleInterval.Monthly);
        trigger.DayOfMonth.Should().Be(1);
    }

    [Fact]
    public void Load_EventTrigger_Parses()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
              event_name: issue.opened
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var trigger = result.Definition!.Trigger;
        trigger!.Type.Should().Be(WorkflowTriggerType.Event);
        trigger.EventName.Should().Be("issue.opened");
    }

    [Fact]
    public void Load_EventTriggerWithPredicates_Parses()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
              event_name: github.issue_comment.created
              if:
                - comment_matches: { pattern: "^/agentweaver:triage$" }
                - not:
                    or:
                      - comment_matches: { pattern: "^/agentweaver:ignore$" }
                      - comment_matches: { pattern: "^/agentweaver:skip$" }
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var trigger = result.Definition!.Trigger;
        trigger!.If.Should().HaveCount(2);
        trigger.If[0].CommentMatches!.Pattern.Should().Be("^/agentweaver:triage$");
        trigger.If[1].Not!.Or.Should().HaveCount(2);
    }

    [Fact]
    public void Load_TriggerMissingType_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              interval: weekly
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("'type'");
    }

    [Fact]
    public void Load_TriggerUnknownType_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: whenever
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("unknown type");
    }

    [Fact]
    public void Load_ScheduleTriggerMissingInterval_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              time_of_day: "09:00"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("interval");
    }

    [Fact]
    public void Load_ScheduleTriggerMalformedTimeOfDay_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: daily
              time_of_day: "9am"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("time_of_day");
    }

    [Fact]
    public void Load_WeeklyScheduleTriggerMissingDayOfWeek_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: weekly
              time_of_day: "09:00"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("day_of_week");
    }

    [Fact]
    public void Load_MonthlyScheduleTriggerDayOfMonthOutOfRange_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: monthly
              day_of_month: 31
              time_of_day: "09:00"
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("day_of_month");
    }

    [Fact]
    public void Load_EventTriggerMissingEventName_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("event_name");
    }

    [Fact]
    public void Load_ScheduleTriggerWithPredicates_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: schedule
              interval: daily
              time_of_day: "09:00"
              if:
                - has_label: { label: "bug" }
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("do not support an 'if' predicate list");
    }

    [Fact]
    public void Load_EventTriggerWithMalformedPredicate_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
              event_name: github.push
              if:
                - ref: { branch: "refs/heads/main" }
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("match_mode");
    }

    [Fact]
    public void Load_EventTriggerWithUnsupportedPredicateForEvent_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
              event_name: github.release.published
              if:
                - has_label: { label: "bug" }
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("github.issues");
    }

    [Fact]
    public void Load_EventTriggerWithCatastrophicBacktrackingShape_Invalid()
    {
        var yaml = BaseYaml + """

            trigger:
              type: event
              event_name: github.issue_comment.created
              if:
                - comment_matches: { pattern: "^(a+)+$" }
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "triage.yaml");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("nested quantifiers");
    }

    [Fact]
    public void Serialize_ThenReload_RoundTripsScheduleTrigger()
    {
        var result = WorkflowDefinitionLoader.Load(BaseYaml + """

            trigger:
              type: schedule
              interval: weekly
              day_of_week: friday
              time_of_day: "17:00"
            """, "triage.yaml");
        result.IsValid.Should().BeTrue(because: result.Error);

        var reserialized = WorkflowDefinitionYamlSerializer.Serialize(result.Definition!);
        var reloaded = WorkflowDefinitionLoader.Load(reserialized, "triage.yaml");

        reloaded.IsValid.Should().BeTrue(because: reloaded.Error);
        var trigger = reloaded.Definition!.Trigger;
        trigger!.Type.Should().Be(WorkflowTriggerType.Schedule);
        trigger.Interval.Should().Be(WorkflowScheduleInterval.Weekly);
        trigger.DayOfWeek.Should().Be(DayOfWeek.Friday);
        trigger.TimeOfDay.Should().Be(new TimeOnly(17, 0));
    }

    [Fact]
    public void Serialize_ThenReload_RoundTripsEventTrigger()
    {
        var result = WorkflowDefinitionLoader.Load(BaseYaml + """

            trigger:
              type: event
              event_name: github.pull_request.opened
              if:
                - has_label: { label: "bug" }
                - or:
                    - base_branch: { branch: "main" }
                    - base_branch: { branch: "release" }
            """, "triage.yaml");
        result.IsValid.Should().BeTrue(because: result.Error);

        var reserialized = WorkflowDefinitionYamlSerializer.Serialize(result.Definition!);
        var reloaded = WorkflowDefinitionLoader.Load(reserialized, "triage.yaml");

        reloaded.IsValid.Should().BeTrue(because: reloaded.Error);
        var trigger = reloaded.Definition!.Trigger;
        trigger!.Type.Should().Be(WorkflowTriggerType.Event);
        trigger.EventName.Should().Be("github.pull_request.opened");
        trigger.If.Should().HaveCount(2);
        trigger.If[1].Or.Should().HaveCount(2);
    }
}
