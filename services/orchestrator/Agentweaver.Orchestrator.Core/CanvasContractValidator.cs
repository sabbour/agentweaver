using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public enum CanvasContractValidationCode
{
    InvalidDescriptor,
    InvalidProviderProfile,
    InvalidSchemaDocument,
    UnsupportedSchemaKeyword,
    SchemaDialectMismatch,
    SchemaTooLarge,
    SchemaDepthExceeded,
    PatternUnsupported,
    InvalidActionId,
    DuplicateActionId,
    ReservedActionId,
    InvalidContentRequirement,
    InvalidScope,
    InvalidInstanceId,
    InvalidContentReference,
    InvalidConfigurationRevision,
    InvalidIdempotencyKey,
    InputTooLarge,
    InputDepthExceeded,
    UnsupportedJsonNumber,
    DescriptorMismatch,
    ScopeMismatch,
    StaleBinding,
    UndeclaredAction,
    InvalidActionInput,
    SchemaMismatch
}

public sealed record CanvasContractValidationIssue(
    CanvasContractValidationCode Code,
    string Path,
    string Message);

public sealed class CanvasContractValidationResult<T> where T : class
{
    private CanvasContractValidationResult(
        T? value,
        ImmutableArray<CanvasContractValidationIssue> issues) =>
        (Value, Issues) = (value, issues);

    public T? Value { get; }
    public ImmutableArray<CanvasContractValidationIssue> Issues { get; }
    public bool IsValid => Value is not null && Issues.IsEmpty;

    internal static CanvasContractValidationResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), []);

    internal static CanvasContractValidationResult<T> Failure(
        ImmutableArray<CanvasContractValidationIssue> issues)
    {
        if (issues.IsEmpty)
            throw new ArgumentException("A failed validation result requires at least one issue.", nameof(issues));
        return new CanvasContractValidationResult<T>(null, issues);
    }
}

public static class CanvasContractValidator
{
    private const string ReservedActionPrefix = "canvas.";

    public static CanvasContractValidationResult<CanvasTypeDescriptor> ValidateDescriptorStructure(
        CanvasTypeDescriptor? descriptor)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        if (descriptor is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "descriptor",
                "A Canvas type descriptor is required.");
            return CanvasContractValidationResult<CanvasTypeDescriptor>.Failure(issues.ToImmutable());
        }

        if (!WorkflowValidationSupport.IsStableId(descriptor.TypeId))
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "descriptor.typeId",
                "Type ID must be a stable identifier.");
        if (!WorkflowValidationSupport.IsOpaqueReference(descriptor.DescriptorRevision))
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "descriptor.descriptorRevision",
                "Descriptor revision must be an opaque reference.");
        if (!IsValidProviderProfile(descriptor.ProviderProfile))
            Add(issues, CanvasContractValidationCode.InvalidProviderProfile, "descriptor.providerProfile",
                "Provider profile identity and versions must be present and valid.");
        if (descriptor.Actions.IsDefault)
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "descriptor.actions",
                "Actions must be an initialized collection.");
        if (descriptor.ContentRequirements.IsDefault)
            Add(issues, CanvasContractValidationCode.InvalidContentRequirement, "descriptor.contentRequirements",
                "Content requirements must be an initialized collection.");

        var actions = descriptor.Actions.IsDefault
            ? ImmutableArray<CanvasActionDescriptor>.Empty
            : descriptor.Actions;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            var path = $"descriptor.actions[{index}]";
            if (action is null)
            {
                Add(issues, CanvasContractValidationCode.InvalidActionId, path,
                    "An action descriptor is required.");
                continue;
            }
            if (!WorkflowValidationSupport.IsStableId(action.ActionId))
                Add(issues, CanvasContractValidationCode.InvalidActionId, path + ".actionId",
                    "Action ID must be a stable identifier.");
            else
            {
                if (action.ActionId.StartsWith(ReservedActionPrefix, StringComparison.OrdinalIgnoreCase))
                    Add(issues, CanvasContractValidationCode.ReservedActionId, path + ".actionId",
                        "Action IDs beginning with the reserved canvas. prefix are not allowed.");
                if (!names.Add(action.ActionId))
                    Add(issues, CanvasContractValidationCode.DuplicateActionId, path + ".actionId",
                        $"Action ID '{action.ActionId}' is duplicated.");
            }
            ValidateSchemaStructure(action.InputSchema, path + ".inputSchema", issues);
            ValidateSchemaStructure(action.OutputSchema, path + ".outputSchema", issues);
        }

        if (descriptor.OpenInputSchema is not null)
            ValidateSchemaStructure(descriptor.OpenInputSchema, "descriptor.openInputSchema", issues);

        var requirements = descriptor.ContentRequirements.IsDefault
            ? ImmutableArray<string>.Empty
            : descriptor.ContentRequirements;
        var requirementIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            if (!WorkflowValidationSupport.IsStableId(requirement))
                Add(issues, CanvasContractValidationCode.InvalidContentRequirement,
                    $"descriptor.contentRequirements[{index}]",
                    "Content requirement must be a stable identifier.");
            else if (!requirementIds.Add(requirement))
                Add(issues, CanvasContractValidationCode.InvalidContentRequirement,
                    $"descriptor.contentRequirements[{index}]",
                    $"Content requirement '{requirement}' is duplicated.");
        }

        if (issues.Count > 0)
            return CanvasContractValidationResult<CanvasTypeDescriptor>.Failure(issues.ToImmutable());

        return CanvasContractValidationResult<CanvasTypeDescriptor>.Success(descriptor with
        {
            OpenInputSchema = descriptor.OpenInputSchema is null
                ? null
                : Clone(descriptor.OpenInputSchema),
            Actions = [.. actions.Select(action => action with
            {
                InputSchema = Clone(action.InputSchema),
                OutputSchema = Clone(action.OutputSchema)
            })],
            ContentRequirements = requirements
        });
    }

    public static CanvasContractValidationResult<CanvasOpenRequest> ValidateOpenRequestStructure(
        CanvasOpenRequest? request,
        CanvasTypeDescriptor? descriptor)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        var validatedDescriptor = ValidateDescriptorStructure(descriptor);
        if (!validatedDescriptor.IsValid)
        {
            issues.AddRange(validatedDescriptor.Issues);
            return CanvasContractValidationResult<CanvasOpenRequest>.Failure(issues.ToImmutable());
        }

        if (request is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "request",
                "A Canvas open request is required.");
            return CanvasContractValidationResult<CanvasOpenRequest>.Failure(issues.ToImmutable());
        }

        ValidateScope(request.Scope, "request.scope", issues);
        if (!WorkflowValidationSupport.IsStableId(request.InstanceId))
            Add(issues, CanvasContractValidationCode.InvalidInstanceId, "request.instanceId",
                "Instance ID must be a stable identifier.");
        if (!MatchesDescriptor(request.ProviderProfile, request.TypeId,
                request.DescriptorRevision, validatedDescriptor.Value!))
            Add(issues, CanvasContractValidationCode.DescriptorMismatch, "request",
                "Open request must use the exact selected provider profile and descriptor revision.");
        ValidateContentReference(request.Content, "request.content", issues);
        if (!WorkflowValidationSupport.IsOpaqueReference(request.ConfigurationRevision))
            Add(issues, CanvasContractValidationCode.InvalidConfigurationRevision,
                "request.configurationRevision",
                "Configuration revision must be an opaque reference.");
        ValidateIdempotencyKey(request.IdempotencyKey, "request.idempotencyKey", issues);
        if (validatedDescriptor.Value!.OpenInputSchema is { } openSchema)
        {
            if (request.Input is not { } input || input.ValueKind == JsonValueKind.Undefined)
                Add(issues, CanvasContractValidationCode.InvalidActionInput, "request.input",
                    "Open input is required by the selected Canvas type.");
            else if (CanvasSchemaSubsetValidator.ValidateInstance(
                         openSchema, input, "request.input") is { } inputIssue)
                issues.Add(inputIssue);
        }
        else if (request.Input is not null)
        {
            Add(issues, CanvasContractValidationCode.InvalidActionInput, "request.input",
                "The selected Canvas type does not declare open input.");
        }

        if (issues.Count > 0)
            return CanvasContractValidationResult<CanvasOpenRequest>.Failure(issues.ToImmutable());
        return CanvasContractValidationResult<CanvasOpenRequest>.Success(request with
        {
            Scope = request.Scope with { },
            Content = request.Content with { },
            Input = request.Input?.Clone()
        });
    }

    public static CanvasContractValidationResult<CanvasReadRequest> ValidateReadRequestStructure(
        CanvasReadRequest? request,
        CanvasInstanceBinding? currentBinding)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        if (request is null || currentBinding is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "request",
                "A read request and current owner binding are required.");
            return CanvasContractValidationResult<CanvasReadRequest>.Failure(issues.ToImmutable());
        }

        ValidateScope(request.Scope, "request.scope", issues);
        if (!WorkflowValidationSupport.IsStableId(request.InstanceId))
            Add(issues, CanvasContractValidationCode.InvalidInstanceId, "request.instanceId",
                "Instance ID must be a stable identifier.");
        ValidateBinding(currentBinding, "currentBinding", issues);
        if (!Equals(request.Scope, currentBinding.Scope))
            Add(issues, CanvasContractValidationCode.ScopeMismatch, "request.scope",
                "Read scope does not match the current Canvas instance scope.");
        if (!string.Equals(request.InstanceId, currentBinding.InstanceId, StringComparison.Ordinal))
            Add(issues, CanvasContractValidationCode.StaleBinding, "request.instanceId",
                "Read request does not identify the current Canvas instance.");

        if (issues.Count > 0)
            return CanvasContractValidationResult<CanvasReadRequest>.Failure(issues.ToImmutable());
        return CanvasContractValidationResult<CanvasReadRequest>.Success(request with
        {
            Scope = request.Scope with { }
        });
    }

    public static CanvasContractValidationResult<CanvasActionRequest> ValidateActionRequestStructure(
        CanvasActionRequest? request,
        CanvasInstanceBinding? currentBinding,
        CanvasTypeDescriptor? descriptor)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        var validatedDescriptor = ValidateDescriptorStructure(descriptor);
        if (!validatedDescriptor.IsValid)
        {
            issues.AddRange(validatedDescriptor.Issues);
            return CanvasContractValidationResult<CanvasActionRequest>.Failure(issues.ToImmutable());
        }
        if (request is null || currentBinding is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "request",
                "An action request and current owner binding are required.");
            return CanvasContractValidationResult<CanvasActionRequest>.Failure(issues.ToImmutable());
        }
        if (request.ExpectedBinding is null)
        {
            ValidateBinding(null, "request.expectedBinding", issues);
            return CanvasContractValidationResult<CanvasActionRequest>.Failure(issues.ToImmutable());
        }

        ValidateBinding(request.ExpectedBinding, "request.expectedBinding", issues);
        ValidateBinding(currentBinding, "currentBinding", issues);
        if (!Equals(request.ExpectedBinding.Scope, currentBinding.Scope))
            Add(issues, CanvasContractValidationCode.ScopeMismatch, "request.expectedBinding.scope",
                "Action scope does not match the current Canvas instance scope.");
        if (!SameBinding(request.ExpectedBinding, currentBinding))
            Add(issues, CanvasContractValidationCode.StaleBinding, "request.expectedBinding",
                "Action request is bound to a stale Canvas revision.");
        if (!MatchesDescriptor(currentBinding.ProviderProfile, currentBinding.TypeId,
                currentBinding.DescriptorRevision, validatedDescriptor.Value!))
            Add(issues, CanvasContractValidationCode.DescriptorMismatch, "descriptor",
                "Descriptor does not match the current Canvas instance binding.");
        var action = validatedDescriptor.Value!.Actions.FirstOrDefault(candidate =>
            string.Equals(candidate.ActionId, request.ActionId, StringComparison.Ordinal));
        if (!WorkflowValidationSupport.IsStableId(request.ActionId) || action is null)
            Add(issues, CanvasContractValidationCode.UndeclaredAction, "request.actionId",
                "Action is not declared by the exact bound Canvas type.");
        ValidateIdempotencyKey(request.IdempotencyKey, "request.idempotencyKey", issues);
        if (request.Input.ValueKind == JsonValueKind.Undefined)
            Add(issues, CanvasContractValidationCode.InvalidActionInput, "request.input",
                "Action input must be a JSON value.");
        else if (action is not null &&
                 CanvasSchemaSubsetValidator.ValidateInstance(
                     action.InputSchema, request.Input, "request.input") is { } inputIssue)
            issues.Add(inputIssue);

        if (issues.Count > 0)
            return CanvasContractValidationResult<CanvasActionRequest>.Failure(issues.ToImmutable());
        return CanvasContractValidationResult<CanvasActionRequest>.Success(request with
        {
            ExpectedBinding = CopyBinding(request.ExpectedBinding),
            Input = request.Input.Clone()
        });
    }

    public static CanvasContractValidationResult<CanvasCloseRequest> ValidateCloseRequestStructure(
        CanvasCloseRequest? request,
        CanvasInstanceBinding? currentBinding)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        if (request is null || currentBinding is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidDescriptor, "request",
                "A close request and current owner binding are required.");
            return CanvasContractValidationResult<CanvasCloseRequest>.Failure(issues.ToImmutable());
        }
        if (request.ExpectedBinding is null)
        {
            ValidateBinding(null, "request.expectedBinding", issues);
            return CanvasContractValidationResult<CanvasCloseRequest>.Failure(issues.ToImmutable());
        }

        ValidateBinding(request.ExpectedBinding, "request.expectedBinding", issues);
        ValidateBinding(currentBinding, "currentBinding", issues);
        if (!Equals(request.ExpectedBinding.Scope, currentBinding.Scope))
            Add(issues, CanvasContractValidationCode.ScopeMismatch, "request.expectedBinding.scope",
                "Close scope does not match the current Canvas instance scope.");
        if (!SameBinding(request.ExpectedBinding, currentBinding))
            Add(issues, CanvasContractValidationCode.StaleBinding, "request.expectedBinding",
                "Close request is bound to a stale Canvas revision.");
        ValidateIdempotencyKey(request.IdempotencyKey, "request.idempotencyKey", issues);

        if (issues.Count > 0)
            return CanvasContractValidationResult<CanvasCloseRequest>.Failure(issues.ToImmutable());
        return CanvasContractValidationResult<CanvasCloseRequest>.Success(request with
        {
            ExpectedBinding = CopyBinding(request.ExpectedBinding)
        });
    }

    private static void ValidateSchemaStructure(
        CanvasJsonSchema? schema,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        issues.AddRange(CanvasSchemaSubsetValidator.ValidateSchema(schema, path));
    }

    private static bool IsValidProviderProfile(CanvasProviderProfile? profile) =>
        profile is not null &&
        WorkflowValidationSupport.IsStableId(profile.ProviderId) &&
        profile.AdapterVersion is { Major: >= 0, Minor: >= 0, Build: >= -1, Revision: >= -1 } &&
        WorkflowValidationSupport.IsStableId(profile.ProfileId) &&
        WorkflowValidationSupport.IsOpaqueReference(profile.ProfileRevision) &&
        WorkflowValidationSupport.IsOpaqueReference(profile.FormatVersion);

    private static bool MatchesDescriptor(
        CanvasProviderProfile? providerProfile,
        string? typeId,
        string? descriptorRevision,
        CanvasTypeDescriptor descriptor) =>
        Equals(providerProfile, descriptor.ProviderProfile) &&
        string.Equals(typeId, descriptor.TypeId, StringComparison.Ordinal) &&
        string.Equals(descriptorRevision, descriptor.DescriptorRevision, StringComparison.Ordinal);

    private static void ValidateScope(
        CanvasRequestScope? scope,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (scope is null ||
            !WorkflowValidationSupport.IsStableId(scope.ProjectId) ||
            !WorkflowValidationSupport.IsStableId(scope.SessionId))
            Add(issues, CanvasContractValidationCode.InvalidScope, path,
                "Project and session scope IDs must be stable identifiers.");
    }

    private static void ValidateContentReference(
        CanvasContentReference? content,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (content is null ||
            !WorkflowValidationSupport.IsOpaqueReference(content.Revision) ||
            content.Sha256 is not { Length: 64 } ||
            !content.Sha256.All(Uri.IsHexDigit))
            Add(issues, CanvasContractValidationCode.InvalidContentReference, path,
                "Content reference requires an opaque revision and a SHA-256 digest.");
    }

    private static void ValidateBinding(
        CanvasInstanceBinding? binding,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (binding is null)
        {
            Add(issues, CanvasContractValidationCode.StaleBinding, path,
                "A current Canvas instance binding is required.");
            return;
        }
        ValidateScope(binding.Scope, path + ".scope", issues);
        if (!WorkflowValidationSupport.IsStableId(binding.InstanceId))
            Add(issues, CanvasContractValidationCode.InvalidInstanceId, path + ".instanceId",
                "Instance ID must be a stable identifier.");
        if (!IsValidProviderProfile(binding.ProviderProfile))
            Add(issues, CanvasContractValidationCode.InvalidProviderProfile, path + ".providerProfile",
                "Provider profile identity and versions must be present and valid.");
        if (!WorkflowValidationSupport.IsStableId(binding.TypeId) ||
            !WorkflowValidationSupport.IsOpaqueReference(binding.DescriptorRevision) ||
            !WorkflowValidationSupport.IsOpaqueReference(binding.ConfigurationRevision) ||
            binding.StateRevision < 0)
            Add(issues, CanvasContractValidationCode.StaleBinding, path,
                "Canvas binding identity or revision is invalid.");
        ValidateContentReference(binding.Content, path + ".content", issues);
    }

    private static bool SameBinding(
        CanvasInstanceBinding? expected,
        CanvasInstanceBinding? current) =>
        expected is not null &&
        current is not null &&
        Equals(expected.Scope, current.Scope) &&
        string.Equals(expected.InstanceId, current.InstanceId, StringComparison.Ordinal) &&
        Equals(expected.ProviderProfile, current.ProviderProfile) &&
        string.Equals(expected.TypeId, current.TypeId, StringComparison.Ordinal) &&
        string.Equals(expected.DescriptorRevision, current.DescriptorRevision, StringComparison.Ordinal) &&
        Equals(expected.Content, current.Content) &&
        string.Equals(expected.ConfigurationRevision, current.ConfigurationRevision, StringComparison.Ordinal) &&
        expected.StateRevision == current.StateRevision;

    private static CanvasInstanceBinding CopyBinding(CanvasInstanceBinding binding) =>
        binding with
        {
            Scope = binding.Scope with { },
            ProviderProfile = binding.ProviderProfile with { },
            Content = binding.Content with { }
        };

    private static CanvasJsonSchema Clone(CanvasJsonSchema schema) =>
        schema with { Definition = schema.Definition.Clone() };

    private static void ValidateIdempotencyKey(
        string? key,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (!WorkflowValidationSupport.IsStableId(key))
            Add(issues, CanvasContractValidationCode.InvalidIdempotencyKey, path,
                "Idempotency key must be a stable identifier.");
    }

    private static void Add(
        ImmutableArray<CanvasContractValidationIssue>.Builder issues,
        CanvasContractValidationCode code,
        string path,
        string message) =>
        issues.Add(new CanvasContractValidationIssue(code, path, message));
}
