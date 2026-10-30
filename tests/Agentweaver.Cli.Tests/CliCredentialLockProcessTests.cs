using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Vault;

namespace Agentweaver.Cli.Tests;

public sealed class CliCredentialLockProcessTests
{
    private const string ProbeFlag = "AGENTWEAVER_CLI_LOCK_PROBE";
    private const string ProbeDirectory = "AGENTWEAVER_CLI_LOCK_DIRECTORY";
    private const string ProbeId = "AGENTWEAVER_CLI_LOCK_ID";
    private const string InFlightRefreshFlag = "AGENTWEAVER_CLI_IN_FLIGHT_REFRESH_PROBE";
    private const string ProbeAccount = "AGENTWEAVER_CLI_LOCK_ACCOUNT";
    private const string Account = "agentweaver-v1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CredentialLockCreatesPrivatePersistentLockFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "lock-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new CliCredentialLock(directory).RunAsync(
                Account, (_, _) => Task.FromResult(true), CancellationToken.None);
            var path = Path.Combine(directory, Account + ".lock");

            Assert.True(result);
            Assert.True(File.Exists(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialLockSerializesCriticalSectionAcrossProcesses()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "lock-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var releasePath = Path.Combine(directory, "release");
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        using var first = StartProbe(directory, firstId);
        using var second = StartProbe(directory, secondId);

        try
        {
            await Task.WhenAll(
                WaitForFileAsync(Path.Combine(directory, firstId + ".ready"), first),
                WaitForFileAsync(Path.Combine(directory, secondId + ".ready"), second));
            await WaitForFileAsync(Path.Combine(directory, firstId + ".entered"), first,
                Path.Combine(directory, secondId + ".entered"));
            await Task.Delay(300);

            var enteredWhileHeld = Directory.GetFiles(directory, "*.entered").Length;
            File.WriteAllText(releasePath, "release");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
            var firstOutput = await first.StandardOutput.ReadToEndAsync();
            var firstError = await first.StandardError.ReadToEndAsync();
            var secondOutput = await second.StandardOutput.ReadToEndAsync();
            var secondError = await second.StandardError.ReadToEndAsync();

            Assert.Equal(1, enteredWhileHeld);
            Assert.True(first.ExitCode == 0, firstOutput + firstError);
            Assert.True(second.ExitCode == 0, secondOutput + secondError);
            Assert.Contains("Passed!", firstOutput + firstError);
            Assert.Contains("Passed!", secondOutput + secondError);
            Assert.True(File.Exists(Path.Combine(directory, firstId + ".completed")));
            Assert.True(File.Exists(Path.Combine(directory, secondId + ".completed")));
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            try { await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialLockProcessProbe()
    {
        if (Environment.GetEnvironmentVariable(ProbeFlag) != "1") return;

        var directory = Environment.GetEnvironmentVariable(ProbeDirectory)
            ?? throw new InvalidOperationException("The lock probe directory was not provided.");
        var id = Environment.GetEnvironmentVariable(ProbeId)
            ?? throw new InvalidOperationException("The lock probe ID was not provided.");
        File.WriteAllText(Path.Combine(directory, id + ".ready"), "ready");
        var gateDirectory = Path.Combine(directory, "locks");
        var releasePath = Path.Combine(directory, "release");
        var lockTimeout = TimeSpan.FromSeconds(20);

        await new CliCredentialLock(gateDirectory, lockTimeout).RunAsync(
            Account,
            async (_, ct) =>
            {
                File.WriteAllText(Path.Combine(directory, id + ".entered"), "entered");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(lockTimeout);
                while (!File.Exists(releasePath))
                    await Task.Delay(20, timeout.Token);
                File.WriteAllText(Path.Combine(directory, id + ".completed"), "completed");
                return true;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task RefreshIntentSurvivesForcedTerminationDuringRefreshBodyRead()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "lock-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var identity = new CliIdentity(
            new CliTarget(
                new Uri("https://gateway.test/"),
                new Uri("https://broker.test/"),
                "agentweaver-cli",
                "https://api.test",
                "tenant-123"),
            ["openid", "api.read", "offline_access"]);
        var account = CliAuthIdentity.Create(identity).Account;
        var process = StartInFlightRefreshProbe(directory, account);
        try
        {
            await WaitForFileAsync(Path.Combine(directory, "refresh-body-reading"), process);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, process.ExitCode);

            var vault = new ProcessTestVault();
            vault.Set(account, "test-only-possibly-consumed-token");
            using var http = new HttpClient(new ThrowingHandler());
            using var service = new CliAuthService(
                http,
                vault,
                new NoopBrowser(),
                new UnusedCallbackFactory(),
                new CliCredentialLock(Path.Combine(directory, "locks"), TimeSpan.FromSeconds(5)));

            var status = await service.StatusAsync(identity, CancellationToken.None);

            Assert.False(status.IsSignedIn);
            Assert.Null(vault.Get(account));
            Assert.False(File.Exists(Path.Combine(directory, "locks", account + ".refreshing")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialLockInFlightRefreshProbe()
    {
        if (Environment.GetEnvironmentVariable(InFlightRefreshFlag) != "1") return;

        var directory = Environment.GetEnvironmentVariable(ProbeDirectory)
            ?? throw new InvalidOperationException("The crash probe directory was not provided.");
        var account = Environment.GetEnvironmentVariable(ProbeAccount)
            ?? throw new InvalidOperationException("The crash probe account was not provided.");
        var identity = new CliIdentity(
            new CliTarget(
                new Uri("https://gateway.test/"),
                new Uri("https://broker.test/"),
                "agentweaver-cli",
                "https://api.test",
                "tenant-123"),
            ["openid", "api.read", "offline_access"]);
        var vault = new ProcessTestVault();
        vault.Set(account, "test-only-possibly-consumed-token");
        using var http = new HttpClient(new CrashProbeHandler(
            Path.Combine(directory, "refresh-body-reading")));
        using var service = new CliAuthService(
            http,
            vault,
            new NoopBrowser(),
            new UnusedCallbackFactory(),
            new CliCredentialLock(Path.Combine(directory, "locks"), TimeSpan.FromSeconds(20)));

        await service.GetBearerAsync(identity, InteractionPolicy.Never, CancellationToken.None);
        throw new InvalidOperationException("The crash probe refresh unexpectedly completed.");
    }

    private static Process StartProbe(string directory, string id)
        => StartTestHost(directory, id, ProbeFlag, "1",
            "FullyQualifiedName~CredentialLockProcessProbe", null);

    private static Process StartInFlightRefreshProbe(string directory, string account) =>
        StartTestHost(directory, Guid.NewGuid().ToString("N"), InFlightRefreshFlag, "1",
            "FullyQualifiedName~CredentialLockInFlightRefreshProbe", account);

    private static Process StartTestHost(
        string directory, string id, string modeVariable, string mode,
        string filter, string? account)
    {
        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Agentweaver.Cli.Tests.csproj"));
        var targetFrameworkDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(
            Path.GetDirectoryName(targetFrameworkDirectory)!).Name;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "test", project, "--no-build", "--no-restore", "--configuration", configuration,
            "--filter", filter,
        })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[modeVariable] = mode;
        startInfo.Environment[ProbeDirectory] = directory;
        startInfo.Environment[ProbeId] = id;
        if (account is not null) startInfo.Environment[ProbeAccount] = account;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the CLI credential-lock probe process.");
    }

    private static async Task WaitForFileAsync(
        string path, Process process, string? alternatePath = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(path) && (alternatePath is null || !File.Exists(alternatePath)))
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"The credential-lock probe exited early ({process.ExitCode}). {output}{error}");
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class ProcessTestVault : ICliCredentialVault
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string account) => _values.GetValueOrDefault(account);
        public void Set(string account, string refreshToken) => _values[account] = refreshToken;
        public void Delete(string account) => _values.TryRemove(account, out _);
    }

    private sealed class NoopBrowser : ICliSystemBrowser
    {
        public Task OpenAsync(Uri authorizationUri, CancellationToken ct) =>
            throw new InvalidOperationException("Recovery must not open a browser.");
    }

    private sealed class UnusedCallbackFactory : IOAuthCallbackListenerFactory
    {
        public IOAuthCallbackListener Start() =>
            throw new InvalidOperationException("Recovery must not start a callback listener.");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Recovery must not contact the Broker.");
    }

    private sealed class CrashProbeHandler(string bodyReadMarker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(
                    """{"issuer":"https://broker.test/","authorization_endpoint":"https://broker.test/connect/authorize","token_endpoint":"https://broker.test/connect/token","jwks_uri":"https://broker.test/.well-known/jwks"}"""));
            if (request.RequestUri.AbsolutePath == "/connect/token")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CrashProbeStream(bodyReadMarker)),
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return Task.FromResult(response);
            }
            throw new InvalidOperationException("Unexpected Broker request.");
        }
    }

    private sealed class CrashProbeStream(string bodyReadMarker) : Stream
    {
        private int _marked;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _marked, 1) == 0)
                File.WriteAllText(bodyReadMarker, "body read started");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
