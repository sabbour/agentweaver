using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.Cli;

internal static class ByokImportCommands
{
    internal static async Task<int> CaptureContextAsync(string[] args, CliRuntimeOptions runtime, HttpClient httpClient,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            var options = CliOptions.Parse(args, new HashSet<string>(StringComparer.Ordinal));
            options.RequireOnly("output", "gateway-url", "broker-issuer", "client-id", "api-audience", "tenant-id");
            var path = options.Single("output");
            if (string.IsNullOrEmpty(path)) throw new InvalidDataException();
            var identity = CliConfiguration.ReadIdentity(["projects.admin"], options: options, unbound: true);
            var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
                .Create(identity);
            var document = await gateway.DiscoverAsync(ct).ConfigureAwait(false);
            ByokImportContract.RequireTarget(document);
            if (gateway is not GatewayClient native) throw ByokImportContract.Invalid();
            var target = await native.ReadImportTargetContextAsync(ct).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                issuer = target.Issuer,
                tenantId = target.TenantId,
                actorId = target.ActorId,
                installationId = target.InstallationId.ToString("D"),
            });
            if (bytes.Length > 64 * 1024) throw new InvalidDataException();
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, fileOptions))
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await output.WriteLineAsync("Target context saved. Compare its installation ID with the trusted intended ID.")
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("Target context capture cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await error.WriteLineAsync("The target context could not be saved.").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> RunAsync(string[] args, CliRuntimeOptions runtime, HttpClient httpClient,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? digest = null;
        string? reference = null;
        string? operationId = null;
        string? candidate = null;
        var completed = new JsonArray();
        var outcome = "rejected";
        var code = "import_invalid_input";
        var message = "The import options or model bindings file are invalid.";
        var exit = 1;
        var dispatched = false;
        try
        {
            var options = CliOptions.Parse(args, new HashSet<string>(StringComparer.Ordinal));
            options.RequireOnly("model-bindings", "file-sha256", "expected-context", "gateway-url", "broker-issuer",
                "client-id", "api-audience", "tenant-id");
            foreach (var target in CliConfiguration.TargetOptionNames.Where(n => n is not ("project-id" or "run-id")))
                _ = options.Single(target);
            var path = options.Single("model-bindings");
            var expected = options.Single("file-sha256");
            var contextPath = options.Single("expected-context");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(contextPath) || expected is null || expected.Length != 64 ||
                !expected.All(char.IsAsciiHexDigit))
                throw new InvalidDataException();

            var bytes = await ReadFileAsync(path, ByokImportOverlay.MaximumFileBytes, ct).ConfigureAwait(false);
            var hash = SHA256.HashData(bytes);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            digest = actual;
            var bindings = ByokImportOverlay.Parse(bytes);
            var expectedContext = await ReadExpectedContextAsync(contextPath, ct).ConfigureAwait(false);
            var prepared = bindings.Select(binding =>
            {
                var id = ByokImportOverlay.OperationId(expectedContext.Issuer, expectedContext.TenantId,
                    expectedContext.InstallationId, hash, binding.Reference);
                var body = binding.Body(id, expectedContext.InstallationId);
                if (Encoding.UTF8.GetByteCount(body.ToJsonString()) > 32 * 1024) throw new InvalidDataException();
                return (Binding: binding, Id: id, Body: body);
            }).ToArray();
            var identity = CliConfiguration.ReadIdentity(["projects.admin"], options: options, unbound: true);
            code = "import_target_unavailable";
            message = "Provider Draft import is unavailable. The Gateway must expose the authorized target-context and provider-create operations.";
            var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
                .Create(identity);
            var document = await gateway.DiscoverAsync(ct).ConfigureAwait(false);
            ByokImportContract.Require(document, prepared);
            if (gateway is not GatewayClient native) throw ByokImportContract.Invalid();
            native.ValidateImportRequests(prepared.Select(item => item.Body));
            code = "import_target_unavailable";
            message = "Target context is unavailable. Sign in with an unbound PlatformAdmin account and check the selected target.";
            if (await native.ReadImportTargetContextAsync(ct).ConfigureAwait(false) != expectedContext)
                throw new ImportTargetMismatchException();
            foreach (var item in prepared)
            {
                reference = item.Binding.Reference;
                operationId = item.Id.ToString("D");
                dispatched = false;
                var response = await native.InvokeImportCreateAsync(
                    item.Body, expectedContext, () => dispatched = true, ct).ConfigureAwait(false);
                if (response.Status == 200)
                {
                    var receipt = ByokImportContract.Receipt(response.Body, item.Binding, item.Id);
                    if (receipt is null)
                    {
                        code = "import_result_unknown";
                        outcome = "unknown";
                        candidate = "byok-" + item.Id.ToString("N");
                        message = UnknownResult;
                        break;
                    }
                    completed.Add(receipt);
                    reference = null;
                    operationId = null;
                    continue;
                }
                var failure = StaticRejection(response);
                code = failure ?? "import_result_unknown";
                outcome = failure is null ? "unknown" : "rejected";
                candidate = failure is null ? "byok-" + item.Id.ToString("N") : null;
                message = response.Status == 503 ? UnavailableResult :
                    failure is null ? UnknownResult : "Provider Draft import was rejected. Resolve the reported code before retrying.";
                break;
            }
            if (reference is null)
            {
                code = string.Empty;
                message = string.Empty;
                exit = 0;
            }
        }
        catch (ExpectedContextException exception)
        {
            code = exception.Code;
            message = ExpectedContextMessage(code);
        }
        catch (ImportTargetMismatchException)
        {
            code = "expected_context_mismatch";
            message = ExpectedContextMessage(code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            code = "import_cancelled";
            message = "Import cancelled.";
            exit = 130;
            if (dispatched)
            {
                outcome = "unknown";
                candidate = "byok-" + Guid.Parse(operationId!).ToString("N");
                message = UnknownResult;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (dispatched)
            {
                outcome = "unknown";
                code = "import_result_unknown";
                candidate = "byok-" + Guid.Parse(operationId!).ToString("N");
                message = UnknownResult;
            }
        }

        JsonObject? stop = exit == 0 ? null : new JsonObject
        {
            ["reference"] = reference,
            ["operationId"] = operationId,
            ["outcome"] = outcome,
            ["code"] = code,
        };
        if (candidate is not null) stop!["expectedProviderId"] = candidate;
        var summary = new JsonObject
        {
            ["fileSha256"] = digest,
            ["completed"] = completed,
            ["stop"] = stop,
        };
        try { await output.WriteLineAsync(summary.ToJsonString()).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await error.WriteLineAsync(UnknownResult).ConfigureAwait(false);
            return 1;
        }
        if (message.Length != 0) await error.WriteLineAsync(message).ConfigureAwait(false);
        return exit;
    }

    private const string UnknownResult =
        "The import result is unknown. Keep the original context file, overlay, digest and arguments. Retry the same import with the same account.";
    private const string UnavailableResult =
        "Provider Draft import is unavailable. The result is unknown. Keep the original context file, overlay, digest and arguments. Retry the same import with the same account after the service is available.";

    private static string? StaticRejection(GatewayResponse response)
    {
        if (response.Body is not JsonObject body || GatewayContract.String(body, "code") is not { } code) return null;
        return (response.Status, code) switch
        {
            (400, "model_provider_draft_invalid_request") or (413, "request_body_too_large") or
            (415, "model_provider_draft_unsupported_media_type") or
            (409, "reference_conflict" or "model_provider_draft_conflict" or "target_installation_mismatch" or
                "reference_reconciliation_required") => code,
            _ => null,
        };
    }

    private static string ExpectedContextMessage(string code) => code switch
    {
        "expected_context_missing" => "Expected context is missing. Stop and reconcile with the original installation.",
        "expected_context_invalid" => "Expected context is invalid. Stop and reconcile with the original installation.",
        "expected_context_unavailable" => "Expected context is unavailable. Restore the original file and retry.",
        _ => "The current account or installation differs from the expected context. Stop and investigate.",
    };

    private sealed class ExpectedContextException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private static async Task<ImportTargetContext> ReadExpectedContextAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await ReadFileAsync(path, 64 * 1024, ct).ConfigureAwait(false); }
        catch (FileNotFoundException) { throw new ExpectedContextException("expected_context_missing"); }
        catch (InvalidDataException) { throw new ExpectedContextException("expected_context_invalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ExpectedContextException("expected_context_unavailable"); }
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                throw new JsonException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (field.Value.ValueKind != JsonValueKind.String ||
                    field.Value.GetString() is not { } value || !fields.TryAdd(field.Name, value))
                    throw new JsonException();
            if (fields.Count != 4 || !fields.TryGetValue("issuer", out var issuer) ||
                !fields.TryGetValue("tenantId", out var tenant) || !fields.TryGetValue("actorId", out var actor) ||
                !fields.TryGetValue("installationId", out var installation) ||
                string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor) ||
                !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || issuer != uri.AbsoluteUri.TrimEnd('/') ||
                !Guid.TryParseExact(installation, "D", out var id) || id == Guid.Empty || installation != id.ToString("D"))
                throw new JsonException();
            return new ImportTargetContext(issuer, tenant, actor, id);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { throw new ExpectedContextException("expected_context_invalid"); }
    }

    private static async Task<byte[]> ReadFileAsync(string path, int maximumBytes, CancellationToken ct)
    {
        await using var stream = OperatingSystem.IsWindows() ? OpenRegularFile(path) : OpenUnixRegularFile(path);
        if (!stream.CanSeek || stream.Length > maximumBytes) throw new InvalidDataException();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, maximumBytes + 1 - (int)memory.Length)), ct).ConfigureAwait(false);
            if (count == 0) break;
            memory.Write(buffer, 0, count);
            if (memory.Length > maximumBytes) throw new InvalidDataException();
        }
        return memory.ToArray();
    }

    private static FileStream OpenRegularFile(string path)
    {
        var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3) throw new FileNotFoundException();
            throw new IOException();
        }
        try
        {
            if (GetFileType(handle) != 1 ||
                !GetFileInformationByHandleEx(handle, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) ||
                (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException();
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream OpenUnixRegularFile(string path)
    {
        var flags = OperatingSystem.IsLinux() ? 0xA0800 :
            OperatingSystem.IsMacOS() ? 0x1000104 : throw new InvalidDataException();
        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException();
            if (error == 40 || OperatingSystem.IsMacOS() && error == 62) throw new InvalidDataException();
            throw new IOException();
        }
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            if (FStat(handle, out var status) != 0 || (status.Mode & 0xF000) != 0x8000)
                throw new InvalidDataException();
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime;
        public long ATimeNsec;
        public long MTime;
        public long MTimeNsec;
        public long CTime;
        public long CTimeNsec;
        public long BirthTime;
        public long BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    // The .NET runtime supplies the same normalized status layout on Linux and macOS.
    [DllImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, out UnixFileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileBasicInfo information, uint size);
}
