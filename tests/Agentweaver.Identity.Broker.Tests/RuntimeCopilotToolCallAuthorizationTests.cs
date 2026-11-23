using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

#pragma warning disable GHCP001 // Permission callback test types are pinned to SDK 1.0.18.
namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeCopilotToolCallAuthorizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string WorkspaceRoot =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    [Theory]
    [InlineData("view", "tool.read")]
    [InlineData("create", "tool.write")]
    [InlineData("edit", "tool.write")]
    [InlineData("bash", "exec.shell")]
    [InlineData("read_bash", "exec.read")]
    [InlineData("write_bash", "exec.write")]
    [InlineData("stop_bash", "exec.stop")]
    public async Task PermissionAuthorizationUsesExactPreToolActionAndStableToolCallId(
        string toolName, string expectedAction)
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var callId = "native-call-" + toolName;
        var calls = new ConcurrentQueue<RuntimeActionRequest>();
        var nativeCalls = new RuntimeNativeCallSequence();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Enqueue(request);
                return Task.CompletedTask;
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);
        var input = Create<PreToolUseHookInput>(new
        {
            sessionId,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            cwd = WorkspaceRoot,
            toolName,
            toolArgs = ToolArguments(toolName)
        });
        Assert.Equal(WorkspaceRoot, input.WorkingDirectory);
        var hookInvocation = CreateHookInvocation(sessionId);

        var hook = await authorization.OnPreToolUseAsync(input, hookInvocation);
        Assert.True(hook?.PermissionDecision == "defer-to-hook", hook?.PermissionDecisionReason);
        Assert.Empty(calls);
        Assert.Equal(0, nativeCalls.Snapshot().ToolEffectSequence);

        var request = PermissionRequest(toolName, callId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        var decision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind);
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);
        var shellDecision = await authorization.OnPermissionRequestAsync(
            SecondaryPermission(toolName, callId), permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, shellDecision.Kind);
        var repeatedDecision = await authorization.OnPermissionRequestAsync(request, permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, repeatedDecision.Kind);
        var repeatedSecondary = await authorization.OnPermissionRequestAsync(
            SecondaryPermission(toolName, callId), permissionInvocation);
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, repeatedSecondary.Kind);

        var invocations = calls.Where(call => call.IsToolInvocation).ToArray();
        Assert.Equal(4, invocations.Length);
        Assert.Equal(2, nativeCalls.Snapshot().ToolEffectSequence);
        Assert.All(invocations, call => Assert.Equal(invocations[0], call));
        var first = invocations[0];
        Assert.Equal(expectedAction, first.ActionId);
        Assert.True(first.IsNativeToolCallback);
        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.Equal(RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request)), first.InputHash);
        Assert.Equal(toolName, first.ToolOwner!.ToolName);
        Assert.Equal(callId, first.ToolOwner.CallId);
        Assert.Equal("invocation", first.ToolOwner.Phase);
        Assert.Equal(sessionId, first.ToolOwner.SdkSessionId);
        Assert.Equal(registration.Binding.TurnId, first.ToolOwner.TurnId);
        var secondary = calls.Where(call => !call.IsToolInvocation).ToArray();
        Assert.Equal(2, secondary.Length);
        Assert.Equal(secondary[0], secondary[1]);
        Assert.NotEqual(first.EventId, secondary[0].EventId);
        Assert.Equal("permission", secondary[0].ToolOwner!.Phase);
        Assert.Equal(first.ToolOwner!.ArgumentDigest, secondary[0].ToolOwner!.InvocationArgumentDigest);
        Assert.Equal(first.EventId, secondary[0].ToolOwner!.InvocationEventId);
        Assert.Equal(RuntimeActionContract.Hash(first), secondary[0].ToolOwner!.InvocationRequestHash);
        Assert.All(calls, call =>
        {
            Assert.Equal(expectedAction, call.ActionId);
            Assert.Equal(registration.RuntimeInstanceId, call.RuntimeInstanceId);
            Assert.Equal(registration.Revision, call.RegistrationRevision);
            Assert.Equal(registration.Binding.ExecutionFence, call.ExecutionFence);
            Assert.Null(call.DispatchId);
        });
    }

    [Fact]
    public async Task ConcurrentPermissionCallbacksRemainBoundToTheirToolCallIds()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var calls = new ConcurrentDictionary<Guid, string>();
        var nativeCalls = new RuntimeNativeCallSequence();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (request, _) =>
            {
                if (request.IsToolInvocation)
                    Assert.True(calls.TryAdd(request.EventId, request.ActionId));
                return Task.CompletedTask;
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);

        var readInput = Create<PreToolUseHookInput>(HookInput(sessionId, "read_bash"));
        var writeInput = Create<PreToolUseHookInput>(HookInput(sessionId, "write_bash"));
        var hookInvocation = CreateHookInvocation(sessionId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        Assert.Equal("defer-to-hook", (await authorization.OnPreToolUseAsync(readInput, hookInvocation))?.PermissionDecision);
        Assert.Equal("defer-to-hook", (await authorization.OnPreToolUseAsync(writeInput, hookInvocation))?.PermissionDecision);

        var readRequest = PermissionRequest("read_bash", "call-read");
        var writeRequest = PermissionRequest("write_bash", "call-write");
        var decisions = await Task.WhenAll(
            authorization.OnPermissionRequestAsync(readRequest, permissionInvocation),
            authorization.OnPermissionRequestAsync(writeRequest, permissionInvocation));

        Assert.All(decisions, decision => Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind));
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.Value == "exec.read");
        Assert.Contains(calls, call => call.Value == "exec.write");
        Assert.Equal(2, nativeCalls.Snapshot().ToolEffectSequence);
    }

    [Fact]
    public async Task MissingMismatchedOrDeniedPermissionCallsFailClosed()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var calls = 0;
        var nativeCalls = new RuntimeNativeCallSequence();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration, sessionId,
            (request, _) =>
            {
                if (!request.IsToolInvocation)
                    return Task.CompletedTask;
                calls++;
                throw new RuntimeAuthorizationException("runtime_action_denied");
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);
        var hookInvocation = CreateHookInvocation(sessionId);
        var permissionInvocation = CreatePermissionInvocation(sessionId);
        var hook = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "read_bash")), hookInvocation);
        Assert.Equal("defer-to-hook", hook?.PermissionDecision);

        var unregisteredPermission = await authorization.OnPermissionRequestAsync(
            PermissionRequest("unregistered-tool", "different-call"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, unregisteredPermission.Kind);
        Assert.Equal(0, calls);

        var unattributedPermission = await authorization.OnPermissionRequestAsync(
            ShellPermissionRequest("call-1"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, unattributedPermission.Kind);
        Assert.Equal(0, calls);

        var denied = await authorization.OnPermissionRequestAsync(
            PermissionRequest("read_bash", "call-1"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_action_denied").Kind, denied.Kind);
        Assert.Equal(1, calls);
        Assert.Equal(0, nativeCalls.Snapshot().ToolEffectSequence);

        var unregistered = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "unregistered-tool")), hookInvocation);
        Assert.Equal("deny", unregistered?.PermissionDecision);

        var deniedShellWithoutHook = await authorization.OnPermissionRequestAsync(
            ShellPermissionRequest("no-hook-call"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_authority_unavailable").Kind, deniedShellWithoutHook.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PathTraversalIsDeniedBeforeOwnerAuthorization()
    {
        var ownerCalls = 0;
        var nativeCalls = new RuntimeNativeCallSequence();
        var sessionId = SessionId();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(),
            sessionId,
            (_, _) =>
            {
                ownerCalls++;
                return Task.CompletedTask;
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);

        var preTool = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "view", new { path = "../outside.txt" })),
            CreateHookInvocation(sessionId));
        Assert.Equal("deny", preTool?.PermissionDecision);

        var permission = await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "traversal", new { path = "../outside.txt" }),
            CreatePermissionInvocation(sessionId));
        Assert.Equal(
            PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
            permission.Kind);
        Assert.Equal(0, ownerCalls);
        Assert.Equal(0, nativeCalls.Snapshot().ToolEffectSequence);
    }

    [Fact]
    public async Task SymlinkEscapeIsDeniedByPreToolAndPermissionCallbacks()
    {
        var nested = WorkspacePath($"nested-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nested);
        var link = Path.Combine(nested, $"link-{Guid.NewGuid():N}.txt");
        var outside = Path.Combine(Path.GetDirectoryName(WorkspaceRoot)!, $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(outside);
            Directory.Delete(nested);
            return;
        }
        catch (IOException)
        {
            File.Delete(outside);
            Directory.Delete(nested);
            return;
        }

        try
        {
            var ownerCalls = 0;
            var nativeCalls = new RuntimeNativeCallSequence();
            var sessionId = SessionId();
            var authorization = new RuntimeCopilotToolCallAuthorization(
                RuntimeCopilotSessionTests.Registration(),
                sessionId,
                (_, _) =>
                {
                    ownerCalls++;
                    return Task.CompletedTask;
                },
                ActiveTurn(),
                nativeCalls,
                WorkspaceRoot);
            var input = await authorization.OnPreToolUseAsync(
                Create<PreToolUseHookInput>(
                    HookInput(sessionId, "view", new { path = Path.GetFileName(link) }, nested)),
                CreateHookInvocation(sessionId));
            Assert.Equal("deny", input?.PermissionDecision);

            var permission = await authorization.OnPermissionRequestAsync(
                PermissionRequest("view", "symlink", new { path = link }),
                CreatePermissionInvocation(sessionId));
            Assert.Equal(
                PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
                permission.Kind);
            Assert.Equal(0, ownerCalls);
            Assert.Equal(0, nativeCalls.Snapshot().ToolEffectSequence);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
            Directory.Delete(nested);
        }
    }

    [Fact]
    public async Task ShellRelativePathsUseTheResolvedWorkingDirectory()
    {
        var nested = WorkspacePath($"nested-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nested);
        var link = Path.Combine(nested, $"link-{Guid.NewGuid():N}.txt");
        var outside = Path.Combine(Path.GetDirectoryName(WorkspaceRoot)!, $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(outside);
            Directory.Delete(nested);
            return;
        }
        catch (IOException)
        {
            File.Delete(outside);
            Directory.Delete(nested);
            return;
        }

        try
        {
            var ownerCalls = new List<RuntimeActionRequest>();
            var callId = "nested-shell-path";
            var authorization = new RuntimeCopilotToolCallAuthorization(
                RuntimeCopilotSessionTests.Registration(),
                SessionId(),
                (request, _) =>
                {
                    ownerCalls.Add(request);
                    return Task.CompletedTask;
                },
                ActiveTurn(),
                new RuntimeNativeCallSequence(),
                WorkspaceRoot);

            Approved(await authorization.OnPermissionRequestAsync(
                PermissionRequest("bash", callId),
                CurrentInvocation()));
            var shell = await authorization.OnPermissionRequestAsync(
                ShellPermissionRequest(
                    callId, nested, [Path.GetFileName(link)]),
                CurrentInvocation());

            Assert.Equal(
                PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
                shell.Kind);
            Assert.Single(ownerCalls);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
            Directory.Delete(nested);
        }
    }

    [Fact]
    public async Task BackslashInUnixFilenameDoesNotHideSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        var link = WorkspacePath($"link-{Guid.NewGuid():N}\\file.txt");
        var outside = Path.Combine(Path.GetDirectoryName(WorkspaceRoot)!, $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(outside);
            return;
        }
        catch (IOException)
        {
            File.Delete(outside);
            return;
        }

        try
        {
            var sessionId = SessionId();
            var authorization = new RuntimeCopilotToolCallAuthorization(
                RuntimeCopilotSessionTests.Registration(),
                sessionId,
                (_, _) => Task.CompletedTask,
                ActiveTurn(),
                new RuntimeNativeCallSequence(),
                WorkspaceRoot);
            var preTool = await authorization.OnPreToolUseAsync(
                Create<PreToolUseHookInput>(
                    HookInput(sessionId, "view", new { path = link })),
                CreateHookInvocation(sessionId));

            Assert.Equal("deny", preTool?.PermissionDecision);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task CallbacksRecheckPathsAfterOwnerAuthorization()
    {
        var link = WorkspacePath($"link-{Guid.NewGuid():N}.txt");
        var outside = Path.Combine(Path.GetDirectoryName(WorkspaceRoot)!, $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
            File.Delete(link);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(outside);
            return;
        }
        catch (IOException)
        {
            File.Delete(outside);
            return;
        }

        try
        {
            var ownerCalls = 0;
            var authorization = new RuntimeCopilotToolCallAuthorization(
                RuntimeCopilotSessionTests.Registration(),
                SessionId(),
                (_, _) =>
                {
                    ownerCalls++;
                    File.CreateSymbolicLink(link, outside);
                    return Task.CompletedTask;
                },
                ActiveTurn(),
                new RuntimeNativeCallSequence(),
                WorkspaceRoot);
            var sessionId = SessionId();
            var preTool = await authorization.OnPreToolUseAsync(
                Create<PreToolUseHookInput>(HookInput(sessionId, "view", new { path = link })),
                CreateHookInvocation(sessionId));
            Assert.Equal("defer-to-hook", preTool?.PermissionDecision);
            Assert.Equal(0, ownerCalls);

            File.Delete(link);
            var permission = await authorization.OnPermissionRequestAsync(
                PermissionRequest("view", "late-symlink", new { path = link }),
                CreatePermissionInvocation(sessionId));
            Assert.Equal(
                PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
                permission.Kind);
            Assert.Equal(1, ownerCalls);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task PatchWithOutsideTargetIsDeniedBeforeWriteAuthorization()
    {
        var ownerCalls = new List<RuntimeActionRequest>();
        var nativeCalls = new RuntimeNativeCallSequence();
        var callId = "outside-patch";
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(),
            SessionId(),
            (request, _) =>
            {
                ownerCalls.Add(request);
                return Task.CompletedTask;
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);

        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("edit", callId),
            CurrentInvocation()));
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);

        var outside = Path.Combine(Path.GetDirectoryName(WorkspaceRoot)!, "outside-patch.txt");
        var write = Create<PermissionRequestWrite>(new
        {
            kind = "write",
            toolCallId = callId,
            requestSandboxBypass = false,
            managedApprovalRequired = false,
            fileName = WorkspacePath("patched.txt"),
            fileEdits = new[]
            {
                new
                {
                    before = new { path = WorkspacePath("source.txt"), content = "old" },
                    after = new { path = outside, content = "new" }
                }
            },
            canOfferSessionApproval = false,
            diff = "patch",
            intention = "apply a patch"
        });

        var decision = await authorization.OnPermissionRequestAsync(write, CurrentInvocation());

        Assert.Equal(
            PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
            decision.Kind);
        Assert.Single(ownerCalls);
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);
    }

    [Fact]
    public async Task OutsideWorkingDirectoriesAreDeniedAtPreToolAndShellPermissionHooks()
    {
        var ownerCalls = new List<RuntimeActionRequest>();
        var nativeCalls = new RuntimeNativeCallSequence();
        var sessionId = SessionId();
        var outside = Path.GetFullPath(Path.Combine(WorkspaceRoot, "..", "outside"));
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(),
            sessionId,
            (request, _) =>
            {
                ownerCalls.Add(request);
                return Task.CompletedTask;
            },
            ActiveTurn(),
            nativeCalls,
            WorkspaceRoot);

        var preTool = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(sessionId, "bash", workingDirectory: outside)),
            CreateHookInvocation(sessionId));
        Assert.Equal("deny", preTool?.PermissionDecision);
        Assert.Empty(ownerCalls);
        Assert.Equal(0, nativeCalls.Snapshot().ToolEffectSequence);

        var callId = "outside-cwd";
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("bash", callId),
            CurrentInvocation()));
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);

        var shell = await authorization.OnPermissionRequestAsync(
            ShellPermissionRequest(callId, outside),
            CurrentInvocation());
        Assert.Equal(
            PermissionDecision.Reject("runtime_tool_workspace_containment_denied").Kind,
            shell.Kind);
        Assert.Single(ownerCalls);
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);
    }

    [Fact]
    public async Task NumericToolDenialRecordsBoundedRejectionBeforeReturningReject()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var sessionId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var turns = ActiveTurn();
        var authorized = new List<RuntimeActionRequest>();
        var rejections = new List<RuntimeNativeToolPermissionRejectionRequest>();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            registration,
            sessionId,
            (request, _) =>
            {
                authorized.Add(request);
                return request.IsToolInvocation
                    ? Task.FromException(new RuntimeAuthorizationException(
                        RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode))
                    : Task.CompletedTask;
            },
            turns,
            new RuntimeNativeCallSequence(),
            WorkspaceRoot,
            (request, _) =>
            {
                rejections.Add(request);
                return Task.CompletedTask;
            });
        var invocation = PermissionRequest("view", "call-limit-denied");

        var decision = await authorization.OnPermissionRequestAsync(invocation, CurrentInvocation());

        Assert.Equal(PermissionDecision.Reject(
            RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode).Kind, decision.Kind);
        var ownerRequest = Assert.Single(authorized);
        var rejection = Assert.Single(rejections).Validate();
        Assert.Equal(ownerRequest.EventId, rejection.AttemptId);
        Assert.Equal(turns.ActiveA2AMessageId, rejection.DispatchId);
        Assert.Equal(RuntimeActionContract.Hash(ownerRequest), rejection.RequestHash);
        Assert.Equal(RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode, rejection.ReasonCode);
        var serialized = JsonSerializer.Serialize(rejection, JsonOptions);
        Assert.DoesNotContain("toolName", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toolArgs", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("call-limit-denied", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectionEvidenceFailureDoesNotChangeNativeDenial()
    {
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(),
            SessionId(),
            (request, _) => request.IsToolInvocation
                ? Task.FromException(new RuntimeAuthorizationException(
                    RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode))
                : Task.CompletedTask,
            ActiveTurn(),
            new RuntimeNativeCallSequence(),
                WorkspaceRoot,
            (_, _) => Task.FromException(new OperationCanceledException()));

        var decision = await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "call-evidence-failure"), CurrentInvocation());

        Assert.Equal(PermissionDecision.Reject(
            RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode).Kind, decision.Kind);
    }

    [Fact]
    public async Task SecondaryPermissionDenialDoesNotRecordInvocationRejection()
    {
        var rejections = 0;
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(),
            SessionId(),
            (request, _) => request.IsToolInvocation
                ? Task.CompletedTask
                : Task.FromException(new RuntimeAuthorizationException(
                    RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode)),
            ActiveTurn(),
            new RuntimeNativeCallSequence(),
            WorkspaceRoot,
            (_, _) =>
            {
                rejections++;
                return Task.CompletedTask;
            });
        var invocation = PermissionRequest("view", "call-secondary-denied");
        _ = await authorization.OnPermissionRequestAsync(invocation, CurrentInvocation());

        var decision = await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "call-secondary-denied"), CurrentInvocation());

        Assert.Equal(PermissionDecision.Reject(
            RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode).Kind, decision.Kind);
        Assert.Equal(0, rejections);
    }

    [Fact]
    public async Task ReadOnlyTurnAllowsViewAndRejectsEveryOtherEnabledTool()
    {
        var calls = new List<RuntimeActionRequest>();
        var turns = ActiveTurn(readOnlyToolsOnly: true);
        var nativeCalls = new RuntimeNativeCallSequence();
        var authorization = new RuntimeCopilotToolCallAuthorization(
            RuntimeCopilotSessionTests.Registration(), SessionId(),
            (request, _) =>
            {
                calls.Add(request);
                return Task.CompletedTask;
            }, turns, nativeCalls, WorkspaceRoot);
        var hookInvocation = CreateHookInvocation(SessionId());
        var permissionInvocation = CurrentInvocation();

        var viewHook = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(SessionId(), "view")), hookInvocation);
        Assert.Equal("defer-to-hook", viewHook?.PermissionDecision);
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "read-only-view"), permissionInvocation));
        Assert.Equal(1, nativeCalls.Snapshot().ToolEffectSequence);
        Approved(await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "read-only-view"), permissionInvocation));

        var writeHook = await authorization.OnPreToolUseAsync(
            Create<PreToolUseHookInput>(HookInput(SessionId(), "edit")), hookInvocation);
        Assert.Equal("deny", writeHook?.PermissionDecision);
        Assert.Equal("runtime_tool_read_only", writeHook?.PermissionDecisionReason);
        var writePermission = await authorization.OnPermissionRequestAsync(
            PermissionRequest("edit", "read-only-edit"), permissionInvocation);
        Assert.Equal(PermissionDecision.Reject("runtime_tool_read_only").Kind, writePermission.Kind);
        Assert.All(calls, request => Assert.Equal("tool.read", request.ActionId));
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task RepeatedInvocationCannotReuseAllowAfterAuthorityRevocation()
    {
        var checks = 0;
        var authorization = Authorization((_, _) =>
        {
            if (++checks == 2)
                throw new RuntimeAuthorizationException("runtime_action_denied");
            return Task.CompletedTask;
        });
        var request = PermissionRequest("view", "native-call-1");
        Approved(await authorization.OnPermissionRequestAsync(request, CurrentInvocation()));
        Rejected(await authorization.OnPermissionRequestAsync(request, CurrentInvocation()));
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task SecondaryPermissionRequiresItsOwnFreshPolicyAfterInvocationReplay()
    {
        var requests = new List<RuntimeActionRequest>();
        var authorization = Authorization((request, _) =>
        {
            requests.Add(request);
            if (!request.IsToolInvocation)
                throw new RuntimeAuthorizationException("runtime_action_denied");
            return Task.CompletedTask;
        });
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        Rejected(await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Assert.Equal(new[] { true, true, false }, requests.Select(request => request.IsToolInvocation));
        Assert.Equal(requests[0], requests[1]);
        Assert.NotEqual(requests[1].EventId, requests[2].EventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledTurnCannotReuseSuccessfulInvocationOrSecondaryPermission(bool secondary)
    {
        using var cancellation = new CancellationTokenSource();
        var turns = new RuntimeCopilotTurnObserver();
        _ = turns.Begin(cancellation.Token, Guid.NewGuid());
        var checks = 0;
        var authorization = Authorization((_, _) => { checks++; return Task.CompletedTask; }, turns);
        var invocation = PermissionRequest("view", "native-call-1");
        Approved(await authorization.OnPermissionRequestAsync(invocation, CurrentInvocation()));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            authorization.OnPermissionRequestAsync(
                secondary ? SecondaryPermission("view", "native-call-1") : invocation,
                CurrentInvocation()));
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task AdmissionWaitCannotApproveAfterItsTurnHasEnded()
    {
        // Pause owner authorization, end this turn, and start another before the old check returns.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = ActiveTurn();
        var authorization = Authorization(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, turns);
        var pending = authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation());
        await entered.Task;
        turns.End();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid());
        release.TrySetResult();
        Rejected(await pending);
    }

    [Fact]
    public async Task TurnAndRegistrationScopeCannotRecoverAnEarlierSecondaryPermission()
    {
        var requests = new List<RuntimeActionRequest>();
        var turns = ActiveTurn();
        var authorization = Authorization((request, _) => { requests.Add(request); return Task.CompletedTask; }, turns);
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        turns.End();
        authorization.Clear();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid());
        Rejected(await authorization.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Approved(await authorization.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        var registration = RuntimeCopilotSessionTests.Registration() with { Revision = 2 };
        var recovered = new RuntimeCopilotToolCallAuthorization(
            registration, SessionId(), (request, _) => { requests.Add(request); return Task.CompletedTask; }, turns,
            new RuntimeNativeCallSequence(), WorkspaceRoot);
        Rejected(await recovered.OnPermissionRequestAsync(
            SecondaryPermission("view", "native-call-1"), CurrentInvocation()));
        Approved(await recovered.OnPermissionRequestAsync(
            PermissionRequest("view", "native-call-1"), CurrentInvocation()));
        Assert.Equal(3, requests.Select(request => request.EventId).Distinct().Count());
    }

    private static RuntimeCopilotToolCallAuthorization Authorization(
        Func<RuntimeActionRequest, CancellationToken, Task> authority, RuntimeCopilotTurnObserver? turns = null) =>
        new(RuntimeCopilotSessionTests.Registration(), SessionId(), authority, turns ?? ActiveTurn(),
            new RuntimeNativeCallSequence(), WorkspaceRoot);

    private static RuntimeCopilotTurnObserver ActiveTurn(bool readOnlyToolsOnly = false)
    {
        var turns = new RuntimeCopilotTurnObserver();
        _ = turns.Begin(CancellationToken.None, Guid.NewGuid(), readOnlyToolsOnly);
        return turns;
    }

    private static string SessionId() =>
        RuntimeContractValidation.NativeSessionId(RuntimeCopilotSessionTests.Registration().Binding);

    private static PermissionInvocation CurrentInvocation() => CreatePermissionInvocation(SessionId());

    private static void Approved(PermissionDecision decision) =>
        Assert.Equal(PermissionDecision.ApproveOnce().Kind, decision.Kind);

    private static void Rejected(PermissionDecision decision) =>
        Assert.Equal(PermissionDecision.Reject("denied").Kind, decision.Kind);

    private static object HookInput(
        string sessionId,
        string toolName,
        object? toolArgs = null,
        string? workingDirectory = null) => new
    {
        sessionId,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        cwd = workingDirectory ?? WorkspaceRoot,
        toolName,
        toolArgs = toolArgs ?? ToolArguments(toolName)
    };

    private static HookInvocation CreateHookInvocation(string sessionId) =>
    Create<HookInvocation>(new { sessionId });

    private static PermissionInvocation CreatePermissionInvocation(string sessionId) =>
    Create<PermissionInvocation>(new { sessionId });

    private static PermissionRequest PermissionRequest(
        string toolName,
        string callId,
        object? toolArgs = null) =>
    Create<PermissionRequestHook>(new
    {
        kind = "hook", toolName, toolCallId = callId, toolArgs = toolArgs ?? ToolArguments(toolName)
    });

    private static PermissionRequest ShellPermissionRequest(
        string callId,
        string? resolvedWorkingDirectory = null,
        string[]? possiblePaths = null) =>
    Create<PermissionRequestShell>(new
    {
        kind = "shell", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, fullCommandText = "echo bounded",
        canOfferSessionApproval = false, hasWriteFileRedirection = false,
        commands = new[] { new { identifier = "echo", readOnly = true } },
        possiblePaths = possiblePaths ?? Array.Empty<string>(), possibleUrls = Array.Empty<object>(),
        resolvedWorkingDirectory = resolvedWorkingDirectory ?? WorkspaceRoot,
        intention = "run a bounded command"
    });

    private static PermissionRequest SecondaryPermission(string toolName, string callId) => toolName switch
    {
    "view" => Create<PermissionRequestRead>(new
    {
        kind = "read", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, path = WorkspacePath("input.txt"),
        intention = "read a bounded input"
    }),
    "create" or "edit" => Create<PermissionRequestWrite>(new
    {
        kind = "write", toolCallId = callId, requestSandboxBypass = false,
        managedApprovalRequired = false, fileName = WorkspacePath("output.txt"), newFileContents = "bounded",
        canOfferSessionApproval = false, diff = "", intention = "write a bounded output"
    }),
    _ => ShellPermissionRequest(callId)
    };

    private static object ToolArguments(string toolName) => toolName switch
    {
        "view" => new { path = WorkspacePath("input.txt") },
        "create" or "edit" => new { file_path = WorkspacePath("output.txt") },
        _ => new { command = "bounded" }
    };

    private static string WorkspacePath(string path) => Path.Combine(WorkspaceRoot, path);

    private static T Create<T>(object value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), JsonOptions)
        ?? throw new InvalidOperationException($"Could not create {typeof(T).Name} test input.");
}

#pragma warning restore GHCP001
