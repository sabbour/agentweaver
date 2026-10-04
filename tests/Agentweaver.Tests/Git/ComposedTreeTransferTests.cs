using Agentweaver.Api.Git;
using Agentweaver.Domain;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Git;

public sealed class ComposedTreeTransferTests
{
    [Fact]
    public void VerifiedTree_TransfersOnlyToIsolatedParent_AndRetriesWithoutMutatingUserBranch()
    {
        var root = Path.Combine(Environment.CurrentDirectory, $"ct-{Guid.NewGuid():N}"[..11]);
        var repositoryPath = Path.Combine(root, "repo");
        var basePath = Path.Combine(root, "wt");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            Repository.Init(repositoryPath);
            using (var repo = new Repository(repositoryPath))
            {
                File.WriteAllText(Path.Combine(repositoryPath, "base.txt"), "before");
                Commit(repo, "initial");
                if (repo.Head.FriendlyName != "main")
                    repo.Branches.Rename(repo.Head, "main");
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Worktrees:BasePath"] = basePath,
                    ["Git:Author:Name"] = "Test",
                    ["Git:Author:Email"] = "test@localhost",
                }).Build();
            var manager = new WorktreeManager(configuration, NullLogger<WorktreeManager>.Instance);
            var parentRunId = RunId.New();
            var parent = manager.AddWorktree(repositoryPath, "main", parentRunId);
            string baseTree;
            string userTip;
            using (var repo = new Repository(repositoryPath))
            {
                baseTree = repo.Branches[parent.BranchName]!.Tip.Tree.Sha;
                userTip = repo.Branches["main"]!.Tip.Sha;
            }

            var integration = manager.AddDetachedWorktree(
                repositoryPath, parent.BranchName, "i");
            string assembledTree;
            using (var repo = new Repository(integration.WorktreePath))
            {
                File.WriteAllText(Path.Combine(integration.WorktreePath, "assembled.txt"), "after");
                assembledTree = Commit(repo, "assembled");
                repo.CreateBranch("agentweaver/integration/test", repo.Head.Tip);
            }

            var transfer = () => manager.TransferComposedTree(
                repositoryPath, parent.WorktreePath, parentRunId, baseTree,
                "agentweaver/integration/test", assembledTree);
            Action wrongTree = () => manager.TransferComposedTree(
                repositoryPath, parent.WorktreePath, parentRunId, baseTree,
                "agentweaver/integration/test", userTip);
            wrongTree.Should().Throw<InvalidOperationException>();
            Action wrongBase = () => manager.TransferComposedTree(
                repositoryPath, parent.WorktreePath, parentRunId, new string('0', 40),
                "agentweaver/integration/test", assembledTree);
            wrongBase.Should().Throw<InvalidOperationException>().WithMessage("*parent tree changed*");
            using (var repo = new Repository(parent.WorktreePath))
                repo.Head.Tip.Tree.Sha.Should().Be(baseTree);

            transfer();
            transfer();
            File.ReadAllText(Path.Combine(parent.WorktreePath, "assembled.txt")).Should().Be("after");
            using (var repo = new Repository(repositoryPath))
                repo.Branches["main"]!.Tip.Sha.Should().Be(userTip);

            File.WriteAllText(Path.Combine(parent.WorktreePath, "untracked.txt"), "dirty");
            transfer.Should().Throw<InvalidOperationException>().WithMessage("*dirty*");
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Commit(Repository repo, string message)
    {
        Commands.Stage(repo, "*");
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        return repo.Commit(message, signature, signature).Tree.Sha;
    }
}
