using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests;

public class RepositoryRootValidatorTests
{
    private static RepositoryRootValidator CreateValidator(
        Dictionary<string, string?>? config = null)
    {
        var builder = new ConfigurationBuilder();
        if (config is not null)
            builder.AddInMemoryCollection(config);
        var configuration = builder.Build();

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger<RepositoryRootValidator>();

        return new RepositoryRootValidator(configuration, logger);
    }

    // --- Rejection tests (no allowlist) ---

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"//server/share")]
    public void Rejects_UNC_paths(string input)
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(input));
        Assert.Contains("absolute path", ex.Message);
    }

    [Theory]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"//?/C:\x")]
    [InlineData(@"\\.\C:\x")]
    [InlineData(@"//./C:\x")]
    public void Rejects_device_paths(string input)
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(input));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public void Rejects_drive_relative_path()
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize("C:foo"));
        Assert.Contains("absolute path", ex.Message);
    }

    [Theory]
    [InlineData(@"foo\bar")]
    [InlineData("relative/path")]
    public void Rejects_relative_paths(string input)
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(input));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public void Rejects_alternate_data_stream()
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(@"C:\repo:stream"));
        Assert.Contains("absolute path", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_empty_or_whitespace(string? input)
    {
        var validator = CreateValidator();
        var ex = Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(input!));
        Assert.Contains("non-empty", ex.Message);
    }

    // --- Permissive mode (no allowlist) ---

    [Fact]
    public void Permissive_mode_accepts_existing_absolute_path()
    {
        var validator = CreateValidator();
        var tempDir = Path.Combine(Path.GetTempPath(), $"repotest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = validator.ValidateAndCanonicalize(tempDir);
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.True(Path.IsPathRooted(result));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Permissive_mode_still_rejects_UNC()
    {
        var validator = CreateValidator();
        Assert.Throws<RunSubmissionValidationException>(() =>
            validator.ValidateAndCanonicalize(@"\\server\share\repo"));
    }

    // --- Allowlist mode ---

    [Fact]
    public void Allowlist_rejects_path_outside_root()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsidePath);
        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var ex = Assert.Throws<RunSubmissionValidationException>(() =>
                validator.ValidateAndCanonicalize(outsidePath));
            Assert.Contains("allowed repository root", ex.Message);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public void Allowlist_accepts_path_inside_root()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var insidePath = Path.Combine(allowedRoot, "myrepo");
        Directory.CreateDirectory(insidePath);
        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var result = validator.ValidateAndCanonicalize(insidePath);
            Assert.StartsWith(Path.GetFullPath(allowedRoot), result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public void Allowlist_does_not_match_prefix_without_separator()
    {
        // /allowed should NOT match /allowed-foo
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var trickPath = allowedRoot + "-extra";
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(trickPath);
        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var ex = Assert.Throws<RunSubmissionValidationException>(() =>
                validator.ValidateAndCanonicalize(trickPath));
            Assert.Contains("allowed repository root", ex.Message);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(trickPath, recursive: true);
        }
    }

    // --- Symlink tests (guarded for privilege) ---

    [Fact]
    public void Symlink_inside_allowlist_is_accepted()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var realTarget = Path.Combine(allowedRoot, "realrepo");
        var symlinkPath = Path.Combine(allowedRoot, "linkedrepo");
        Directory.CreateDirectory(realTarget);

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, realTarget);
        }
        catch (UnauthorizedAccessException)
        {
            // Symlink creation requires admin or developer mode on Windows — skip.
            Directory.Delete(allowedRoot, recursive: true);
            return;
        }
        catch (IOException)
        {
            // Symlink creation requires elevated privileges — skip.
            Directory.Delete(allowedRoot, recursive: true);
            return;
        }

        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var result = validator.ValidateAndCanonicalize(symlinkPath);
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.Equal(
                Path.GetFullPath(realTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            try { Directory.Delete(symlinkPath); } catch { }
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public void Symlink_alias_returns_same_resolved_repository_identity()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var realTarget = Path.Combine(allowedRoot, "realrepo");
        var symlinkPath = Path.Combine(allowedRoot, "linkedrepo");
        Directory.CreateDirectory(realTarget);

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, realTarget);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(allowedRoot, recursive: true);
            return;
        }
        catch (IOException)
        {
            Directory.Delete(allowedRoot, recursive: true);
            return;
        }

        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var realResult = validator.ValidateAndCanonicalize(realTarget);
            var aliasResult = validator.ValidateAndCanonicalize(symlinkPath);

            Assert.Equal(realResult, aliasResult, ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            try { Directory.Delete(symlinkPath); } catch { }
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public void Symlink_resolving_outside_allowlist_is_rejected()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-{Guid.NewGuid():N}");
        var outsideTarget = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(allowedRoot, "escaped-link");

        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsideTarget);

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, outsideTarget);
        }
        catch (UnauthorizedAccessException)
        {
            // Symlink creation requires admin or developer mode on Windows — skip.
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(outsideTarget, recursive: true);
            return;
        }
        catch (IOException)
        {
            // Symlink creation requires elevated privileges — skip.
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(outsideTarget, recursive: true);
            return;
        }

        try
        {
            var validator = CreateValidator(new Dictionary<string, string?>
            {
                ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
            });

            var ex = Assert.Throws<RunSubmissionValidationException>(() =>
                validator.ValidateAndCanonicalize(symlinkPath));
            Assert.Contains("allowed repository root", ex.Message);
        }
        finally
        {
            try { Directory.Delete(symlinkPath); } catch { }
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(outsideTarget, recursive: true);
        }
    }

    // --- Integration test ---

    [Fact]
    public async Task Integration_allowlist_rejects_outside_path()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"int-allowed-{Guid.NewGuid():N}");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"int-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsidePath);

        // Init a git repo in the outside path so it passes the git check
        LibGit2Sharp.Repository.Init(outsidePath);

        try
        {
            await using var factory = new AgentweaverWebApplicationFactory()
                .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
                    });
                }));

            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AgentweaverWebApplicationFactory.TestApiKey}");

            var response = await client.PostAsJsonAsync("/api/runs", new
            {
                repository_path = outsidePath,
                originating_branch = "main",
                task = "test task",
                model_source = "github-copilot"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.Contains("allowed repository root", body!.Error);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public async Task Integration_allowlist_does_not_reject_inside_path_for_path_reason()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"int-allowed-{Guid.NewGuid():N}");
        var insidePath = Path.Combine(allowedRoot, "myrepo");
        Directory.CreateDirectory(insidePath);

        // Init a git repo so the path validation passes (may still fail on branch-not-found)
        LibGit2Sharp.Repository.Init(insidePath);

        try
        {
            await using var factory = new AgentweaverWebApplicationFactory()
                .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Runs:AllowedRepositoryRoots:0"] = allowedRoot
                    });
                }));

            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AgentweaverWebApplicationFactory.TestApiKey}");

            var response = await client.PostAsJsonAsync("/api/runs", new
            {
                repository_path = insidePath,
                originating_branch = "main",
                task = "test task",
                model_source = "github-copilot"
            });

            // It may 400 for branch-not-found, but NOT for the path rejection message
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                Assert.DoesNotContain("allowed repository root", body!.Error);
            }
            // If it's 202 (run started), that's fine too — means path was accepted
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    private record ErrorResponse
    {
        public string Error { get; init; } = "";
    }
}
