using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using WorkspaceRequest = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspaceRequest;
using PreparationReceipt = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed partial class AgentSandboxProvider
{
    private const string BuildTestPrivateStateVolumeName = "buildtest-private-state";

    public async Task<PreparationReceipt?> PrepareWorkspaceAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var context = await VerifyBuildTestContextAsync(
            request, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken).ConfigureAwait(false);
        var resource = await ReadExactWorkspacePodAsync(
            request, context, pod, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken).ConfigureAwait(false);
        var completed = await ReadPreparationReceiptAsync(request, context, pod, resource, cancellationToken)
            .ConfigureAwait(false);
        if (completed is not null)
            return completed;
        if (!WorkspaceContainerRunning(resource, "initContainerStatuses",
                SandboxBuildTestLimits.WorkspacePreparationContainerName))
            return null;
        try
        {
            var bytes = await _client.ExchangeBuildTestWorkspaceAsync(
                pod, WorkspaceFingerprint(request), request.AcceptedCommand.WorkspaceSeed ??
                    throw ProviderResourceMismatch("The native preparation has no accepted seed bytes."),
                preparingWorkspace: true, SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken,
                WorkspaceExecutionCommand(request, context, preparing: true, "prepare")).ConfigureAwait(false);
            var receipt = JsonSerializer.Deserialize<PreparationReceipt>(bytes.AsSpan(), BuildTestJsonOptions)
                ?? throw ProviderResourceMismatch("The native preparation returned no receipt.");
            receipt.Validate(request.AcceptedCommand, WorkspaceFingerprint(request), pod);
            await ReadExactWorkspacePodAsync(
                request, context, pod, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken)
                .ConfigureAwait(false);
            return receipt;
        }
        catch (SandboxProviderException)
        {
            resource = await ReadExactWorkspacePodAsync(
                request, context, pod, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken)
                .ConfigureAwait(false);
            completed = await ReadPreparationReceiptAsync(request, context, pod, resource, cancellationToken)
                .ConfigureAwait(false);
            if (completed is not null)
                return completed;
            throw;
        }
        catch (JsonException)
        {
            throw ProviderResourceMismatch("The native preparation returned invalid receipt metadata.");
        }
        catch (ArgumentException)
        {
            throw ProviderResourceMismatch("The native preparation receipt differs from its accepted operation.");
        }
    }

    public async Task<SandboxBuildTestWorkspaceCaptureReference?> CaptureWorkspaceAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        PreparationReceipt preparation,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var context = await VerifyWorkspaceCaptureContextAsync(
            request, preparation, cancellationToken).ConfigureAwait(false);
        var resource = await ReadExactWorkspacePodAsync(
            request, context, pod, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken).ConfigureAwait(false);
        if (!WorkspaceContainerRunning(resource, "containerStatuses",
                SandboxBuildTestLimits.WorkspaceCaptureContainerName))
            return null;
        try
        {
            var bytes = await _client.ExchangeBuildTestWorkspaceAsync(
                pod, WorkspaceFingerprint(request), request.AcceptedCommand.WorkspaceSeed ??
                    throw ProviderResourceMismatch("The native capture has no accepted seed bytes."),
                preparingWorkspace: false, SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken,
                WorkspaceExecutionCommand(request, context, preparing: false, "capture")).ConfigureAwait(false);
            var capture = JsonSerializer.Deserialize<SandboxBuildTestWorkspaceCaptureReference>(
                bytes.AsSpan(), BuildTestJsonOptions)
                ?? throw ProviderResourceMismatch("The native capture returned no exact reference.");
            capture.Validate(request.AcceptedCommand, WorkspaceFingerprint(request), pod);
            await VerifyWorkspaceCaptureContextAsync(request, preparation, cancellationToken).ConfigureAwait(false);
            await ReadExactWorkspacePodAsync(
                request, context, pod, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken)
                .ConfigureAwait(false);
            return capture;
        }
        catch (JsonException)
        {
            throw new SandboxProviderException("buildtest_workspace_capture_invalid",
                "The native capture returned invalid metadata.", true);
        }
        catch (ArgumentException)
        {
            throw new SandboxProviderException("buildtest_workspace_capture_invalid",
                "The native capture differs from its accepted operation.", true);
        }
        catch (SandboxProviderException failure) when (!failure.EffectMayHaveApplied)
        {
            throw new SandboxProviderException(failure.Code, failure.Message, true);
        }
    }

    public async Task<ImmutableArray<byte>> ReadWorkspaceCaptureAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        PreparationReceipt preparation,
        SandboxBuildTestWorkspaceCaptureReference capture,
        CancellationToken cancellationToken)
    {
        request.Validate();
        capture.Validate(request.AcceptedCommand, WorkspaceFingerprint(request), pod);
        var context = await VerifyWorkspaceCaptureContextAsync(
            request, preparation, cancellationToken).ConfigureAwait(false);
        await ReadExactWorkspacePodAsync(
            request, context, pod, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken)
            .ConfigureAwait(false);
        var bytes = await _client.ExchangeBuildTestWorkspaceAsync(
            pod, WorkspaceFingerprint(request), ImmutableArray<byte>.Empty, preparingWorkspace: false,
            checked((int)capture.DocumentByteLength), cancellationToken,
            WorkspaceExecutionCommand(request, context, preparing: false, "export")).ConfigureAwait(false);
        if (bytes.Length != capture.DocumentByteLength ||
            Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())) !=
                capture.DocumentSha256)
            throw ProviderResourceMismatch("The native export differs from its sealed capture reference.");
        await VerifyWorkspaceCaptureContextAsync(request, preparation, cancellationToken).ConfigureAwait(false);
        await ReadExactWorkspacePodAsync(
            request, context, pod, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken)
            .ConfigureAwait(false);
        return bytes;
    }

    private async Task<BuildTestContext> VerifyWorkspaceCaptureContextAsync(
        SandboxBuildTestProviderRequest request,
        PreparationReceipt preparation,
        CancellationToken cancellationToken)
    {
        var context = await VerifyBuildTestContextAsync(
            request, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken).ConfigureAwait(false);
        await RequireSuccessfulCommandPodAsync(request, context, cancellationToken).ConfigureAwait(false);
        var command = await GetAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw ProviderResourceMismatch("The native prepared command Pod is missing.");
        preparation.Validate(request.AcceptedCommand, WorkspaceFingerprint(request), command);
        return context;
    }

    private async Task<JsonElement> ReadExactWorkspacePodAsync(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        SandboxBuildTestPodReference pod,
        string role,
        CancellationToken cancellationToken)
    {
        var resource = await _client.GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false)
            ?? throw ProviderResourceMismatch("The exact native workspace Pod is missing.");
        var current = ValidateBuildTestPod(resource, request, context, role);
        if (current.Uid != pod.Uid)
            throw ProviderResourceMismatch("The native workspace Pod UID changed.");
        return resource;
    }

    private async Task<PreparationReceipt?> ReadPreparationReceiptAsync(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        SandboxBuildTestPodReference pod,
        JsonElement resource,
        CancellationToken cancellationToken)
    {
        var statuses = TryGetArray(TryGetObject(resource, "status") ?? default, "initContainerStatuses");
        var completed = statuses is { } entries && entries.EnumerateArray().Any(entry =>
            ReadOptionalString(entry, "name") == SandboxBuildTestLimits.WorkspacePreparationContainerName &&
            TryGetObject(entry, "state") is { } state && TryGetObject(state, "terminated") is { } terminal &&
            TryGetInt64(terminal, "exitCode", out var exitCode) && exitCode == 0);
        if (!completed)
            return null;
        var output = await _client.ReadPodLogsBoundedAsync(
            pod, SandboxBuildTestLimits.WorkspacePreparationContainerName,
            SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken).ConfigureAwait(false);
        if (output.Truncated)
            throw ProviderResourceMismatch("The native preparation receipt was truncated.");
        try
        {
            var receipt = JsonSerializer.Deserialize<PreparationReceipt>(
                output.CapturedBytes.AsSpan(), BuildTestJsonOptions)
                ?? throw ProviderResourceMismatch("The native preparation receipt is missing.");
            receipt.Validate(request.AcceptedCommand, WorkspaceFingerprint(request), pod);
            await ReadExactWorkspacePodAsync(
                request, context, pod, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        catch (JsonException)
        {
            throw ProviderResourceMismatch("The saved native preparation receipt has invalid metadata.");
        }
        catch (ArgumentException)
        {
            throw ProviderResourceMismatch("The saved native preparation receipt differs from its accepted operation.");
        }
    }

    private static bool WorkspaceContainerRunning(JsonElement pod, string statusesName, string containerName) =>
        TryGetObject(pod, "status") is { } status && TryGetArray(status, statusesName) is { } entries &&
        entries.EnumerateArray().Any(entry => ReadOptionalString(entry, "name") == containerName &&
            TryGetObject(entry, "state") is { } state && TryGetObject(state, "running") is not null);

    private static string WorkspaceFingerprint(SandboxBuildTestProviderRequest request) =>
        new SandboxBuildTestApiRequest(request.AcceptedCommand.Checkpoint, request.ExpectedBinding)
            .ComputeRequestFingerprint(request.AcceptedCommand);

    private static WorkspaceRequest CreateWorkspaceRequest(
        SandboxBuildTestProviderRequest request, BuildTestContext context, bool preparing) =>
        new(request.AcceptedCommand.OperationId, request.AcceptedCommand.ImmutableHash,
            WorkspaceFingerprint(request), request.AcceptedCommand.Checkpoint,
            request.AcceptedCommand.WorkspaceSource ??
                throw ProviderResourceMismatch("The native workspace request has no accepted source."),
            context.Descriptor.WorkspaceMountPath, preparing, request.AcceptedCommand.Outputs);

    private static ImmutableArray<string> WorkspaceHelperArguments(
        SandboxBuildTestProviderRequest request, BuildTestContext context, bool preparing, string action)
    {
        var workspace = CreateWorkspaceRequest(request, context, preparing).Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace, BuildTestJsonOptions);
        if (bytes.Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes)
            throw ProviderResourceMismatch("The native workspace metadata exceeds its byte limit.");
        var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return request.AcceptedCommand.ExecutionOptions.CollectorAssemblyArguments
            .Add(SandboxBuildTestLimits.WorkspaceCollectorMode).Add(encoded).Add(action);
    }

    private static ImmutableArray<string> WorkspaceExecutionCommand(
        SandboxBuildTestProviderRequest request, BuildTestContext context, bool preparing, string action) =>
        WorkspaceHelperArguments(request, context, preparing, action)
            .Insert(0, request.AcceptedCommand.ExecutionOptions.CollectorExecutableReference);

    private static JsonElement AddWorkspacePodContainers(
        JsonElement manifest, SandboxBuildTestProviderRequest request, BuildTestContext context, bool collector)
    {
        var node = JsonNode.Parse(manifest.GetRawText())!;
        var spec = node["spec"]!.AsObject();
        var container = spec["containers"]![0]!.AsObject();
        var privateVolume = new JsonObject
        {
            ["name"] = BuildTestPrivateStateVolumeName,
            ["emptyDir"] = new JsonObject { ["sizeLimit"] = request.AcceptedCommand.ExecutionOptions.EphemeralStorageLimit }
        };
        spec["volumes"]!.AsArray().Add(privateVolume);
        var stateMount = new JsonObject
        {
            ["name"] = BuildTestPrivateStateVolumeName, ["mountPath"] = "/state", ["readOnly"] = false
        };
        if (collector)
        {
            spec.Remove("activeDeadlineSeconds");
            container["volumeMounts"]!.AsArray().Add(stateMount);
        }
        else
        {
            var preparation = container.DeepClone().AsObject();
            preparation["name"] = SandboxBuildTestLimits.WorkspacePreparationContainerName;
            preparation["image"] = request.AcceptedCommand.ExecutionOptions.CollectorImageReference;
            preparation["command"] = JsonSerializer.SerializeToNode(
                new[] { request.AcceptedCommand.ExecutionOptions.CollectorExecutableReference });
            preparation["args"] = JsonSerializer.SerializeToNode(
                WorkspaceHelperArguments(request, context, preparing: true, "hold"));
            preparation["workingDir"] = context.Descriptor.WorkspaceMountPath;
            preparation["volumeMounts"]!.AsArray().Add(stateMount);
            preparation["env"] = JsonSerializer.SerializeToNode(new[]
            {
                new { name = BuildTestCollectorPodUidEnvironmentVariable,
                    valueFrom = new { fieldRef = new { apiVersion = "v1", fieldPath = "metadata.uid" } } }
            });
            spec["initContainers"] = new JsonArray(preparation);
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private static void ValidateWorkspacePodContainers(
        JsonElement spec, SandboxBuildTestProviderRequest request, BuildTestContext context, bool collector)
    {
        var options = request.AcceptedCommand.ExecutionOptions;
        var commandMounts = RequiredArray(RequiredArray(spec, "containers")[0], "volumeMounts");
        if (commandMounts.EnumerateArray().Any(mount => mount.TryGetProperty("subPath", out _) ||
                mount.TryGetProperty("subPathExpr", out _) || mount.TryGetProperty("mountPropagation", out _)))
            throw ProviderResourceMismatch("The prepared command workspace mount was mutated.");
        var volume = RequiredArray(spec, "volumes")[1];
        if (RequiredArray(spec, "volumes").GetArrayLength() != 2 ||
            ReadOptionalString(volume, "name") != BuildTestPrivateStateVolumeName ||
            ReadOptionalString(volume, "emptyDir", "sizeLimit") != options.EphemeralStorageLimit ||
            volume.EnumerateObject().Count() != 2)
            throw ProviderResourceMismatch("The native helper private state volume was mutated.");
        var helper = collector ? RequiredArray(spec, "containers")[0] : RequiredArray(spec, "initContainers")[0];
        var mounts = RequiredArray(helper, "volumeMounts");
        var privateMount = mounts[1];
        if (mounts.GetArrayLength() != 2 ||
            ReadOptionalString(privateMount, "name") != BuildTestPrivateStateVolumeName ||
            ReadOptionalString(privateMount, "mountPath") != "/state" ||
            !TryGetBoolean(privateMount, "readOnly", out var readOnly) || readOnly ||
            mounts.EnumerateArray().Any(mount => mount.TryGetProperty("subPath", out _) ||
                mount.TryGetProperty("subPathExpr", out _) || mount.TryGetProperty("mountPropagation", out _)) ||
            TryGetArray(helper, "envFrom") is { } envFrom && envFrom.GetArrayLength() != 0 ||
            !CollectorUidEnvironmentMatches(helper) ||
            !ContainerResourcesMatch(helper, options) || !ContainerSecurityContextMatches(helper))
            throw ProviderResourceMismatch("The native helper private state or security settings were mutated.");
        if (!collector &&
            (ReadOptionalString(helper, "name") != SandboxBuildTestLimits.WorkspacePreparationContainerName ||
             ReadOptionalString(helper, "image") != options.CollectorImageReference ||
             ReadOptionalString(helper, "imagePullPolicy") != "Always" ||
             ReadOptionalString(helper, "workingDir") != context.Descriptor.WorkspaceMountPath ||
             !JsonStringArrayMatches(helper, "command", [options.CollectorExecutableReference]) ||
             !JsonStringArrayMatches(helper, "args", WorkspaceHelperArguments(request, context, true, "hold")) ||
             !WorkspaceVolumeMatches(spec, helper, context, readOnly: false, hasPrivateState: true)))
            throw ProviderResourceMismatch("The native preparation init container differs from its pinned profile.");
    }
}
