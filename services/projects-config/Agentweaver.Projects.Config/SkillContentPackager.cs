using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>One uploaded file and its path relative to the selected file or folder.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillPackageFileRequest
{
    public required string RelativePath { get; init; }
    public required byte[] Content { get; init; }
}

/// <summary>Form fields for a skill that a user writes in the client.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillPackageDraftRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Instructions { get; init; }
}

/// <summary>
/// Request to turn uploaded files, one ZIP archive, or draft form fields into skill candidates.
/// Exactly one of <see cref="Files"/>, <see cref="Archive"/>, or <see cref="Draft"/> is required.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageSkillContentRequest
{
    public ImmutableArray<SkillPackageFileRequest?>? Files { get; init; }
    public byte[]? Archive { get; init; }
    public SkillPackageDraftRequest? Draft { get; init; }
}

/// <summary>
/// One skill found in the upload. A valid package has a candidate and preview that the client passes,
/// unchanged, to the project import route. An invalid package has an error and no candidate.
/// </summary>
public sealed record SkillContentPackage(
    string Location,
    SkillContentCandidateRequest? Candidate,
    SkillContentPreview? Preview,
    string? Error,
    ImmutableArray<string> SkippedPaths);

/// <summary>Packages found in one upload, ordered by location.</summary>
public sealed record SkillContentPackageResult(ImmutableArray<SkillContentPackage> Skills);

/// <summary>
/// Groups uploaded files, ZIP entries, or draft fields into skill candidates and validates each one.
/// It does not store content, change project state, or run any uploaded file.
/// </summary>
public static class SkillContentPackager
{
    public const int MaxUploadFiles = 1024;
    /// <summary>Keeps the base64 JSON request under the 3 MiB route limit.</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024;
    private const string SkillFileName = "SKILL.md";

    /// <summary>Builds candidates from exactly one input kind in the request.</summary>
    /// <exception cref="SkillContentValidationException">The request shape, a path, or the archive is invalid, or no SKILL.md exists.</exception>
    /// <param name="request">The uploaded files, archive, or draft fields.</param>
    /// <param name="preview">The owner preview, so package and import previews use the same digest and size rules.</param>
    public static SkillContentPackageResult Package(
        PackageSkillContentRequest request,
        Func<SkillContentCandidate, SkillContentPreview> preview)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        var inputs = (request.Files is not null ? 1 : 0) + (request.Archive is not null ? 1 : 0) +
            (request.Draft is not null ? 1 : 0);
        if (inputs != 1)
            throw new SkillContentValidationException("Send exactly one of files, archive, or draft.");

        if (request.Draft is not null)
            return FromDraft(request.Draft, preview);
        if (request.Archive is not null)
            return FromFiles(ReadArchive(request.Archive), preview);

        var files = request.Files!.Value;
        if (files.IsDefaultOrEmpty)
            throw new SkillContentValidationException("No files were uploaded.");
        if (files.Length > MaxUploadFiles)
            throw new SkillContentValidationException($"The upload has more than {MaxUploadFiles} files.");
        long total = 0;
        var uploaded = new List<(string Path, byte[] Content)>(files.Length);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || file.Content is null)
                throw new SkillContentValidationException("Each uploaded file requires a path and content.");
            total += file.Content.LongLength;
            if (total > MaxUploadBytes)
                throw new SkillContentValidationException($"The upload exceeds {MaxUploadBytes / (1024 * 1024)} MB.");
            uploaded.Add((NormalizePath(file.RelativePath), file.Content));
        }
        return FromFiles(uploaded, preview);
    }

    /// <summary>Writes the draft fields as SKILL.md frontmatter and instructions, using the 0.x layout.</summary>
    public static string ComposeSkillMarkdown(string name, string? description, string instructions)
    {
        var trimmedName = name.Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? trimmedName : description.Trim();
        return $"---\nname: {QuoteYaml(trimmedName)}\ndescription: {QuoteYaml(trimmedDescription)}\n---\n\n{instructions.Trim()}\n";
    }

    private static SkillContentPackageResult FromDraft(
        SkillPackageDraftRequest draft,
        Func<SkillContentCandidate, SkillContentPreview> preview)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new SkillContentValidationException("A skill name is required.");
        if (string.IsNullOrWhiteSpace(draft.Instructions))
            throw new SkillContentValidationException("Skill instructions are required.");
        var markdown = Encoding.UTF8.GetBytes(ComposeSkillMarkdown(draft.Name, draft.Description, draft.Instructions));
        return new SkillContentPackageResult([Build(SkillFileName, markdown, [], [], preview)]);
    }

    /// <summary>
    /// Treats every SKILL.md as a skill root. Files under a root become its resources, except files that belong
    /// to a nested skill root. Binary or oversized files are skipped and listed, so the preview shows what is kept.
    /// </summary>
    private static SkillContentPackageResult FromFiles(
        IReadOnlyList<(string Path, byte[] Content)> files,
        Func<SkillContentCandidate, SkillContentPreview> preview)
    {
        var duplicates = files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
            throw new SkillContentValidationException($"The upload contains duplicate path '{duplicates.Key}'.");

        var roots = files
            .Where(file => IsSkillFile(file.Path))
            .Select(file => RootOf(file.Path))
            .OrderBy(root => root, StringComparer.Ordinal)
            .ToList();
        if (roots.Count == 0)
            throw new SkillContentValidationException("No SKILL.md was found in the upload.");
        if (roots.Count > SkillRuntimeContentContract.MaxSkillCount)
            throw new SkillContentValidationException(
                $"The upload has more than {SkillRuntimeContentContract.MaxSkillCount} skills.");

        var packages = ImmutableArray.CreateBuilder<SkillContentPackage>(roots.Count);
        foreach (var root in roots)
        {
            var prefix = root.Length == 0 ? string.Empty : root + "/";
            var markdown = files.First(file => file.Path == prefix + SkillFileName).Content;
            var resources = new List<(string Path, byte[] Content)>();
            var skipped = new List<string>();
            foreach (var file in files)
            {
                if (!file.Path.StartsWith(prefix, StringComparison.Ordinal) || IsSkillFile(file.Path))
                    continue;
                var relative = file.Path[prefix.Length..];
                if (roots.Any(other => other.Length > root.Length &&
                    file.Path.StartsWith(other + "/", StringComparison.Ordinal)))
                    continue;
                if (file.Content.Length > SkillContentValidator.MaxResourceBytes || file.Content.Contains((byte)0))
                {
                    skipped.Add(relative);
                    continue;
                }
                resources.Add((relative, file.Content));
            }
            packages.Add(Build(root.Length == 0 ? SkillFileName : root, markdown, resources, skipped, preview));
        }
        return new SkillContentPackageResult(packages.ToImmutable());
    }

    private static SkillContentPackage Build(
        string location,
        byte[] markdown,
        IReadOnlyList<(string Path, byte[] Content)> resources,
        IReadOnlyList<string> skipped,
        Func<SkillContentCandidate, SkillContentPreview> preview)
    {
        var request = new SkillContentCandidateRequest
        {
            SkillMarkdown = markdown,
            Resources = resources
                .OrderBy(resource => resource.Path, StringComparer.Ordinal)
                .Select(resource => (SkillContentResourceRequest?)new SkillContentResourceRequest
                {
                    RelativePath = resource.Path,
                    Content = resource.Content,
                })
                .ToImmutableArray(),
        };
        var skippedPaths = skipped.OrderBy(path => path, StringComparer.Ordinal).ToImmutableArray();
        try
        {
            return new SkillContentPackage(location, request, preview(request.ToCandidate()), null, skippedPaths);
        }
        catch (SkillContentValidationException exception)
        {
            return new SkillContentPackage(location, null, null, exception.Message, skippedPaths);
        }
    }

    /// <summary>
    /// Expands a ZIP archive with entry-count, per-entry, and total-size limits applied while reading,
    /// so an archive cannot expand past the limits in memory.
    /// </summary>
    private static List<(string Path, byte[] Content)> ReadArchive(byte[] archive)
    {
        var files = new List<(string Path, byte[] Content)>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue;
                if (files.Count >= MaxUploadFiles)
                    throw new SkillContentValidationException($"The archive has more than {MaxUploadFiles} files.");
                var path = NormalizePath(entry.FullName);
                using var stream = entry.Open();
                var cap = Math.Max(SkillContentValidator.MaxSkillMarkdownBytes, SkillContentValidator.MaxResourceBytes) + 1;
                var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                var oversized = false;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes)
                        throw new SkillContentValidationException(
                            $"The archive expands to more than {MaxUploadBytes / (1024 * 1024)} MB.");
                    if (buffer.Length + read > cap)
                    {
                        oversized = true;
                        continue;
                    }
                    buffer.Write(chunk, 0, read);
                }
                // An oversized entry keeps one byte past the limit so validation reports or skips it as too large.
                files.Add((path, oversized ? new byte[cap] : buffer.ToArray()));
            }
        }
        catch (InvalidDataException)
        {
            throw new SkillContentValidationException("The archive is not a valid ZIP file.");
        }
        return files;
    }

    private static string NormalizePath(string raw)
    {
        try
        {
            return SkillRuntimeContentContract.NormalizeResourcePath(raw);
        }
        catch (ArgumentException exception)
        {
            throw new SkillContentValidationException(exception.Message);
        }
    }

    private static bool IsSkillFile(string path) =>
        path.Equals(SkillFileName, StringComparison.Ordinal) ||
        path.EndsWith("/" + SkillFileName, StringComparison.Ordinal);

    private static string RootOf(string skillFilePath) =>
        skillFilePath.Length == SkillFileName.Length ? string.Empty : skillFilePath[..^(SkillFileName.Length + 1)];

    private static string QuoteYaml(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
