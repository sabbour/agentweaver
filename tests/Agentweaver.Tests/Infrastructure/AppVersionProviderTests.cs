using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Infrastructure;

/// <summary>
/// Guards the version-badge-provenance fix: the running container's IMAGE_TAG/GIT_SHA env vars
/// (plumbed in via each Dockerfile's `ENV IMAGE_TAG=${IMAGE_TAG}` / `ENV GIT_SHA=${GIT_SHA}`,
/// see apps/Agentweaver.Api/Dockerfile) must take priority over the static VERSION file for a
/// real release build, while a SHA-tagged `azure:upgrade`/`azure:deploy-from-local` build (or
/// local `dotnet run` outside Docker) must fall back to the VERSION file for the base semver.
/// </summary>
public sealed class AppVersionProviderTests : IDisposable
{
    private readonly string _dir;

    public AppVersionProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"app-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "VERSION"), "0.9.70\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", null);
        Environment.SetEnvironmentVariable("GIT_SHA", null);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WhenImageTagIsRealSemver_UsesImageTagAsVersion_AndHidesGitSha()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "0.9.71");
        Environment.SetEnvironmentVariable("GIT_SHA", "a1c11f1");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeTrue();
        provider.Version.Should().Be("0.9.71");
        provider.GitSha.Should().BeNull();
    }

    [Fact]
    public void WhenImageTagHasVPrefix_StillTreatedAsRelease_AndPrefixStripped()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "v0.9.71");
        Environment.SetEnvironmentVariable("GIT_SHA", "a1c11f1");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeTrue();
        provider.Version.Should().Be("0.9.71");
    }

    [Fact]
    public void WhenImageTagIsGitSha_FallsBackToVersionFile_AndSurfacesGitSha()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "a1c11f1");
        Environment.SetEnvironmentVariable("GIT_SHA", "a1c11f1");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeFalse();
        provider.Version.Should().Be("0.9.70");
        provider.GitSha.Should().Be("a1c11f1");
    }

    [Fact]
    public void WhenImageTagIsDevPlaceholder_FallsBackToVersionFile_WithNoGitSha()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "dev");
        Environment.SetEnvironmentVariable("GIT_SHA", "unknown");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeFalse();
        provider.Version.Should().Be("0.9.70");
        provider.GitSha.Should().BeNull();
    }

    [Fact]
    public void WhenNoEnvVarsSet_FallsBackToVersionFile_LikeLocalDotnetRun()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", null);
        Environment.SetEnvironmentVariable("GIT_SHA", null);

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeFalse();
        provider.Version.Should().Be("0.9.70");
        provider.GitSha.Should().BeNull();
    }

    [Fact]
    public void WhenGitShaIsFullLength_TruncatesToSevenCharShortSha()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "a1c11f1234567890abcdef1234567890abcdef12");
        Environment.SetEnvironmentVariable("GIT_SHA", "a1c11f1234567890abcdef1234567890abcdef12");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.IsRelease.Should().BeFalse();
        provider.GitSha.Should().Be("a1c11f1");
        provider.GitSha.Should().HaveLength(7);
    }

    [Fact]
    public void WhenGitShaIsAlreadyShort_LeavesItUnchanged()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", "a1c11f1");
        Environment.SetEnvironmentVariable("GIT_SHA", "a1c");

        var provider = new AppVersionProvider(new FakeWebHostEnvironment(_dir));

        provider.GitSha.Should().Be("a1c");
    }

    [Fact]
    public void WhenVersionFileIsMissing_FallsBackToZeroVersion()
    {
        Environment.SetEnvironmentVariable("IMAGE_TAG", null);
        Environment.SetEnvironmentVariable("GIT_SHA", null);

        var emptyDir = Path.Combine(Path.GetTempPath(), $"app-version-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var provider = new AppVersionProvider(new FakeWebHostEnvironment(emptyDir));
            provider.Version.Should().Be("0.0.0");
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; } = "Agentweaver.Tests";
        public IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Testing";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
    }
}
