using System.IO;
using FluentAssertions;
using Agentweaver.Api.Sandbox.Preview;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Unit coverage for <see cref="PreviewCommandResolver"/> (spec-006 §11). The security-critical
/// assertion is BLOCKER 4: every resolved command forces an ALL-INTERFACE (0.0.0.0) bind so the
/// Gateway can reach the app via the pod IP — a loopback-only bind would pass the pod's local health
/// probe yet yield a silent no-URL.
/// </summary>
public sealed class PreviewCommandResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly PreviewCommandResolver _resolver = new();

    public PreviewCommandResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-resolver-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void WritePackageJson(string scripts) =>
        File.WriteAllText(Path.Combine(_dir, "package.json"), $$"""{ "scripts": { {{scripts}} } }""");

    private static void WritePackageJson(string directory, string scripts)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), $$"""{ "scripts": { {{scripts}} } }""");
    }

    [Fact]
    public void Vite_ForcesHostAllInterfaces()
    {
        WritePackageJson("\"dev\": \"vite\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("--host 0.0.0.0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void Next_ForcesHostAllInterfaces()
    {
        WritePackageJson("\"start\": \"next start\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("-H 0.0.0.0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void AspNet_SetsAspNetCoreUrlsToAllInterfaces()
    {
        File.WriteAllText(Path.Combine(_dir, "Web.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("ASPNETCORE_URLS=http://0.0.0.0:0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void NodeServer_SetsHostEnvToAllInterfaces()
    {
        File.WriteAllText(Path.Combine(_dir, "server.js"), "require('http');");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("HOST=0.0.0.0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void Python_ForcesHostAllInterfaces()
    {
        File.WriteAllText(Path.Combine(_dir, "app.py"), "print('hi')");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("--host 0.0.0.0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void Python_UsesPython3Interpreter()
    {
        // Regression guard for #426: the sandbox image only has `python3` on PATH, not a bare
        // `python` symlink (confirmed live: `which python` -> not found, `which python3` ->
        // /usr/bin/python3). A resolved command that invokes bare `python` fails every preview
        // for a Python app with "process_exited: exitCode=127 ... python: not found".
        File.WriteAllText(Path.Combine(_dir, "app.py"), "print('hi')");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().StartWith("python3 ",
            because: "the sandbox image does not have a bare `python` binary on PATH (#426)");
    }

    [Fact]
    public void GenericNpmScript_SetsHostEnvToAllInterfaces()
    {
        WritePackageJson("\"start\": \"node build/index.js\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("HOST=0.0.0.0");
        AssertNoLoopback(result.Command!);
        AssertNoHardcodedPort(result.Command!);
    }

    [Fact]
    public void EmptyWorktree_IsUnresolved()
    {
        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeFalse();
        result.Command.Should().BeNull();
        result.Source.Should().Be("unresolved");
    }

    [Fact]
    public void MissingDirectory_IsUnresolved()
    {
        var result = _resolver.Resolve(Path.Combine(_dir, "does-not-exist"));

        result.Resolved.Should().BeFalse();
    }

    [Fact]
    public void RootWins()
    {
        WritePackageJson("\"dev\": \"vite\"");
        WritePackageJson(Path.Combine(_dir, "client"), "\"dev\": \"vite\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Cwd.Should().Be(_dir);
        result.Source.Should().Be("package.json:dev");
    }

    [Fact]
    public void NestedAppClientVite()
    {
        WritePackageJson(Path.Combine(_dir, "app", "client"), "\"dev\": \"vite\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Command.Should().Contain("--host 0.0.0.0");
        result.Cwd.Should().EndWith(Path.Combine("app", "client"));
    }

    [Fact]
    public void ClientServerPrefersClient()
    {
        WritePackageJson(Path.Combine(_dir, "client"), "\"dev\": \"vite\"");
        WritePackageJson(Path.Combine(_dir, "server"), "\"start\": \"node server.js\"");

        var result = _resolver.Resolve(_dir);

        result.Cwd.Should().EndWith("client");
        result.Cwd.Should().NotEndWith("server");
    }

    [Fact]
    public void NodeModulesNeverSelected()
    {
        WritePackageJson(Path.Combine(_dir, "node_modules", "foo"), "\"dev\": \"vite\"");

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeFalse();
    }

    [Fact]
    public void NestedCsproj()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Web.csproj"), string.Empty);

        var result = _resolver.Resolve(_dir);

        result.Resolved.Should().BeTrue();
        result.Source.Should().Be("csproj");
        result.Cwd.Should().EndWith("app");
    }

    private static void AssertNoLoopback(string command)
    {
        command.Should().NotContain("127.0.0.1");
        command.Should().NotContain("localhost");
    }

    // spec-006 preview-forwarder item C: the platform must NEVER pin the app's port (no hardcoded
    // 3000). The app keeps its framework default; the AgentHost discovers the actual bound port and
    // the forwarder exposes a dynamically-chosen public port.
    private static void AssertNoHardcodedPort(string command)
    {
        command.Should().NotContain("PORT=3000");
        command.Should().NotContain("--port 3000");
        command.Should().NotContain("-p 3000");
        command.Should().NotContain(":3000");
    }
}
