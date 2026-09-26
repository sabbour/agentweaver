using System.Text;
using Agentweaver.Api.Git;
using FluentAssertions;
using LibGit2Sharp;

namespace Agentweaver.Tests.Git;

public sealed class GitReferenceTransactionTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    [Fact]
    public async Task CompareExchange_ConcurrentCallers_ExactlyOneApplies()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var first = CreateCommit(repoPath, oldOid, "first.txt", "first");
        var second = CreateCommit(repoPath, oldOid, "second.txt", "second");

        var results = await Task.WhenAll(
            Task.Run(() => GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", first, oldOid)),
            Task.Run(() => GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", second, oldOid)));

        results.Count(result => result.Kind == GitReferenceUpdateKind.Applied).Should().Be(1);
        results.Count(result => result.Kind == GitReferenceUpdateKind.ExpectedOldMismatch).Should().Be(1);
        Resolve(repoPath, "refs/heads/main").Should().BeOneOf(first, second);
    }

    [Fact]
    public void CompareExchange_ExternalMovement_ReportsObservedTipWithoutRewind()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "intended.txt", "intended");
        var external = CreateCommit(repoPath, oldOid, "external.txt", "external");
        UpdateRef(repoPath, "refs/heads/main", external);

        var result = GitReferenceTransaction.CompareExchange(
            repoPath, "refs/heads/main", intended, oldOid);

        result.Kind.Should().Be(GitReferenceUpdateKind.ExpectedOldMismatch);
        result.CurrentOid.Should().Be(external);
        Resolve(repoPath, "refs/heads/main").Should().Be(external);
    }

    [Fact]
    public void CompareExchange_MissingRefAndObject_AreTyped()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var missing = new string('f', oldOid.Length);

        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/missing", oldOid, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.MissingRef);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", missing, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.MissingObject);
    }

    [Fact]
    public void CompareExchange_SymbolicRef_IsRejected()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        RunGit(repoPath, "symbolic-ref", "refs/heads/alias", "refs/heads/main");

        var result = GitReferenceTransaction.CompareExchange(
            repoPath, "refs/heads/alias", oldOid, oldOid);

        result.Kind.Should().Be(GitReferenceUpdateKind.SymbolicRef);
    }

    [Fact]
    public void CheckedOutLinkedWorktree_CasThenConverge_UpdatesExactCheckoutAndPreservesUntracked()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        File.WriteAllText(Path.Combine(linkedPath, "harmless.tmp"), "keep");

        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out var error);
        error.Should().BeNull();

        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);
        GitReferenceTransaction.ConvergeCheckedOut(repoPath, proof, intended)
            .Kind.Should().Be(GitCheckoutConvergenceKind.Converged);

        Resolve(linkedPath, "HEAD").Should().Be(intended);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("updated");
        File.ReadAllText(Path.Combine(linkedPath, "harmless.tmp")).Should().Be("keep");
    }

    [Fact]
    public void CrashAfterRefBeforeConvergence_CanResumeFromCapturedProof()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);

        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("initial");

        GitReferenceTransaction.ConvergeCheckedOut(repoPath, proof, intended)
            .Kind.Should().Be(GitCheckoutConvergenceKind.Converged);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("updated");
    }

    [Fact]
    public void CrashAfterRefThenNewTrackedEdit_ParksWithoutDestroyingOrRewinding()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);

        File.WriteAllText(Path.Combine(linkedPath, "tracked.txt"), "new edit");
        var result = GitReferenceTransaction.ConvergeCheckedOut(repoPath, proof, intended);

        result.Kind.Should().Be(GitCheckoutConvergenceKind.PreStateMismatch);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("new edit");
        Resolve(repoPath, "refs/heads/main").Should().Be(intended);
    }

    [Fact]
    public void CaptureCheckedOutPreState_StagedEdit_IsRejected()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        File.WriteAllText(Path.Combine(linkedPath, "tracked.txt"), "staged edit");
        RunGit(linkedPath, "add", "tracked.txt");

        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out var error);

        proof.Should().BeNull();
        error.Should().Be("index_dirty");
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("staged edit");
    }

    [Fact]
    public void Convergence_TrackedEditArrivesAtMaterialization_ParksWithoutDestroying()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);

        var result = GitReferenceTransaction.ConvergeCheckedOut(
            repoPath,
            proof,
            intended,
            () => File.WriteAllText(Path.Combine(linkedPath, "tracked.txt"), "racing edit"));

        result.Kind.Should().Be(GitCheckoutConvergenceKind.PreStateMismatch);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("racing edit");
        Resolve(repoPath, "refs/heads/main").Should().Be(intended);
    }

    [Fact]
    public void Convergence_RefMovesAtMaterialization_DoesNotRewind()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var later = CreateCommit(repoPath, intended, "later.txt", "later");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);

        var result = GitReferenceTransaction.ConvergeCheckedOut(
            repoPath,
            proof,
            intended,
            () => UpdateRef(repoPath, "refs/heads/main", later));

        result.Kind.Should().Be(GitCheckoutConvergenceKind.PreStateMismatch);
        Resolve(repoPath, "refs/heads/main").Should().Be(later);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("initial");
    }

    [Fact]
    public void Convergence_WorktreeSwitchesBranchAtMaterialization_DoesNotTouchNewCheckout()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var linkedPath = AddLinkedWorktree(repoPath, "main");
        using (var repo = new Repository(repoPath))
            repo.CreateBranch("other", repo.Lookup<Commit>(oldOid));
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);

        var result = GitReferenceTransaction.ConvergeCheckedOut(
            repoPath,
            proof,
            intended,
            () => RunGit(linkedPath, "checkout", "other"));

        result.Kind.Should().Be(GitCheckoutConvergenceKind.PreStateMismatch);
        Resolve(linkedPath, "HEAD").Should().Be(oldOid);
        File.ReadAllText(Path.Combine(linkedPath, "tracked.txt")).Should().Be("initial");
        Resolve(repoPath, "refs/heads/main").Should().Be(intended);
    }

    [Fact]
    public void Convergence_WhenRefMovedAgain_DoesNotRewind()
    {
        var repoPath = CreateRepository();
        var oldOid = Resolve(repoPath, "refs/heads/main");
        var intended = CreateCommit(repoPath, oldOid, "tracked.txt", "updated");
        var later = CreateCommit(repoPath, intended, "later.txt", "later");
        AddLinkedWorktree(repoPath, "main");
        var proof = GitReferenceTransaction.CaptureCheckedOutPreState(
            repoPath, "refs/heads/main", oldOid, out _);
        GitReferenceTransaction.CompareExchange(
                repoPath, "refs/heads/main", intended, oldOid)
            .Kind.Should().Be(GitReferenceUpdateKind.Applied);
        UpdateRef(repoPath, "refs/heads/main", later);

        GitReferenceTransaction.ConvergeCheckedOut(repoPath, proof, intended)
            .Kind.Should().Be(GitCheckoutConvergenceKind.RefMoved);
        Resolve(repoPath, "refs/heads/main").Should().Be(later);
    }

    private string CreateRepository()
    {
        var path = NewTempDirectory("repo");
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "tracked.txt"), "initial");
        Commands.Stage(repo, "tracked.txt");
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var commit = repo.Commit("initial", signature, signature);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");
        var workspace = repo.CreateBranch("_workspace", commit);
        Commands.Checkout(repo, workspace);
        return path;
    }

    private string AddLinkedWorktree(string repoPath, string branch)
    {
        var path = NewTempDirectory("worktree", create: false);
        RunGit(repoPath, "worktree", "add", path, branch);
        return path;
    }

    private static string CreateCommit(
        string repoPath,
        string parentOid,
        string path,
        string content)
    {
        using var repo = new Repository(repoPath);
        var parent = repo.Lookup<Commit>(parentOid)!;
        var definition = TreeDefinition.From(parent.Tree);
        definition.Add(path, CreateBlob(repo, content), Mode.NonExecutableFile);
        var tree = repo.ObjectDatabase.CreateTree(definition);
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        return repo.ObjectDatabase.CreateCommit(
            signature, signature, path, tree, [parent], prettifyMessage: true).Sha;
    }

    private static Blob CreateBlob(Repository repo, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return repo.ObjectDatabase.CreateBlob(stream);
    }

    private static string Resolve(string repositoryPath, string revision)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Lookup<Commit>(revision)!.Sha;
    }

    private static void UpdateRef(string repositoryPath, string fullRef, string oid)
    {
        using var repo = new Repository(repositoryPath);
        repo.Refs.UpdateTarget(repo.Refs[fullRef], oid);
    }

    private static void RunGit(string repositoryPath, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, stderr);
    }

    private string NewTempDirectory(string suffix, bool create = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentweaver-git-cas-{suffix}-{Guid.NewGuid():N}");
        _tempDirectories.Add(path);
        if (create)
            Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempDirectories.OrderByDescending(path => path.Length))
        {
            if (!Directory.Exists(path))
                continue;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }
}
