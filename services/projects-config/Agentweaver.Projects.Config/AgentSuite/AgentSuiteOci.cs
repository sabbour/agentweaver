using System.Text;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>An in-memory OCI image layout: regular files by relative path, and the directories they imply.</summary>
internal sealed class LayoutFs
{
    public LayoutFs(IReadOnlyDictionary<string, byte[]> files)
    {
        Files = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        // Tar archives may omit directory entries, so derive every parent directory from file paths
        // (e.g. "blobs/sha256/abc" adds "blobs/sha256" and "blobs").
        foreach (var path in Files.Keys)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                Dirs.Add(path[..slash]);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
    }

    public Dictionary<string, byte[]> Files { get; }

    public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal);

    /// <summary>Go <c>os.Lstat</c> error text for a missing entry; paths are relative to the layout root.</summary>
    public static string NotFound(string path) => $"lstat {path}: no such file or directory";
}

/// <summary>A descriptor of the AgentSuite OCI artifact together with its exact blob bytes.</summary>
internal sealed record OciBlob(string Digest, string MediaType, byte[] Data);

/// <summary>The loaded OCI artifact (upstream <c>loadOCILayoutContent</c> result).</summary>
internal sealed class OciLoad
{
    /// <summary>The single index manifest descriptor digest, when known (also set for rejected layouts).</summary>
    public string? IndexedManifestDigest { get; set; }

    public OciBlob? Manifest { get; set; }
    public OciBlob? Config { get; set; }
    public OciBlob? Layer { get; set; }
    public ContentSet? Content { get; set; }
}

/// <summary>Ports upstream oci.go over an in-memory layout, plus the Agentweaver outer tar reader.</summary>
internal static class AgentSuiteOci
{
    // Hard limits that keep a hostile layout from using too much memory or CPU.
    public const int MaxOciIndexEntries = 1000;
    public const long MaxIndexedBlobBytes = 1L << 30;
    public const int MaxLayoutEntries = 4096;

    /// <summary>
    /// Reads the uploaded POSIX tar of a layout directory. Returns an Agentweaver profile error message
    /// (code <c>agentsuite_archive_invalid</c>) for unsafe or unsupported outer entries.
    /// </summary>
    public static string? ReadLayoutTar(byte[] archive, out LayoutFs? layout)
    {
        layout = null;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        // Case-folded names seen so far, to catch "A.json" vs "a.json" (they collide on Windows/macOS).
        var folded = new HashSet<string>(StringComparer.Ordinal);
        // The reader stops with a limit error if more bytes than the archive limit are ever read.
        var tar = new GoTarReader(new GoReader(new MemoryStream(archive, writable: false), AgentSuiteValidator.MaxArchiveBytes + 1L));
        var count = 0;
        while (true)
        {
            var error = tar.Next(out var header);
            // "EOF" is the normal end of the archive.
            if (error == GoReader.Eof)
                break;
            if (error is not null)
                return $"The archive is not a valid tar file: {error}.";
            if (++count > MaxLayoutEntries)
                return $"The archive has more than {MaxLayoutEntries} entries.";
            // Normalize names: drop leading "./" (common when archiving with "tar -C dir .").
            var name = header!.Name;
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            // Directory names usually end with '/'; remove it so they compare equal to file parents.
            var isDir = header.Typeflag == GoTarReader.TypeDir;
            if (isDir)
                name = name.TrimEnd('/');
            // Skip the entry for the layout root directory itself.
            if (isDir && name is "" or ".")
                continue;
            // Reject paths that could escape the layout or are ambiguous: absolute paths,
            // backslashes, NUL bytes, empty segments ("a//b"), and "." or ".." segments.
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains('\0') ||
                name.Split('/').Any(part => part is "" or "." or ".."))
                return $"The archive entry {Go.Quote(header.Name)} has an unsafe path.";
            // Only plain files and directories are allowed; no links, devices or FIFOs.
            if (header.Typeflag is not (GoTarReader.TypeReg or GoTarReader.TypeDir))
                return $"The archive entry {Go.Quote(header.Name)} is not a regular file or directory.";
            if (files.ContainsKey(name) || dirs.Contains(name))
                return $"The archive entry {Go.Quote(header.Name)} is duplicated.";
            if (!folded.Add(Go.FoldCase(name)))
                return $"The archive entry {Go.Quote(header.Name)} collides with another entry by case.";
            if (isDir)
            {
                dirs.Add(name);
                continue;
            }
            // Ask for one extra byte so we can tell "exactly Size bytes" from a body that is short.
            var readError = tar.ReadAll(header.Size + 1, out var data);
            if (readError is not null || data.LongLength != header.Size)
                return $"The archive entry {Go.Quote(header.Name)} is truncated.";
            files[name] = data;
        }
        var fs = new LayoutFs(files);
        // Add explicit directory entries too (including empty directories).
        foreach (var dir in dirs)
            fs.Dirs.Add(dir);
        // A path cannot be a file and also the parent directory of another file.
        foreach (var path in files.Keys)
        {
            if (fs.Dirs.Contains(path))
                return $"The archive entry {Go.Quote(path)} is both a file and a directory.";
        }
        layout = fs;
        return null;
    }

    /// <summary>Upstream <c>loadOCILayoutContent</c>; returns the Go error text or null.</summary>
    public static string? LoadLayout(LayoutFs fs, OciLoad load)
    {
        // Step 1: "oci-layout" is a small JSON marker file that declares the layout version.
        if (ReadRegularFile(fs, "oci-layout", AgentSuiteJson.MaxBytes, out var layoutBytes) is { } layoutReadError)
            return $"read oci-layout: {layoutReadError}";
        if (AgentSuiteJson.Unmarshal(layoutBytes, AgentSuiteTypes.OciLayout, out var layoutNode) is { } layoutError)
            return $"oci-layout: {layoutError}";
        if (new J(layoutNode)["imageLayoutVersion"].S != AgentSuiteTypes.LayoutVersion)
            return $"oci-layout imageLayoutVersion must be {AgentSuiteTypes.LayoutVersion}";
        // Step 2: "index.json" is the entry point; it lists manifest descriptors
        // (digest + size + media type) that point to blobs under "blobs/sha256/".
        if (ReadRegularFile(fs, "index.json", AgentSuiteJson.MaxBytes, out var indexBytes) is { } indexReadError)
            return $"read index.json: {indexReadError}";
        if (AgentSuiteJson.Unmarshal(indexBytes, AgentSuiteTypes.OciIndex, out var indexNode) is { } indexError)
            return $"index.json: {indexError}";
        var index = new J(indexNode);
        // mediaType is optional in an index, but if present it must be the OCI index type.
        var indexMediaType = index["mediaType"].S;
        if (index["schemaVersion"].I != 2 || indexMediaType.Length != 0 && indexMediaType != AgentSuiteTypes.OciIndexMediaType)
            return "index.json is not an OCI image index";
        var manifests = index["manifests"].A;
        if (manifests.Count > MaxOciIndexEntries)
            return $"index.json has more than {MaxOciIndexEntries} manifest descriptors";
        // A suite layout holds exactly one artifact, so exactly one manifest.
        if (manifests.Count != 1)
            return $"index.json must contain exactly one manifest descriptor, found {manifests.Count}";
        // Remember the digest early so it can be reported even if later checks reject the layout.
        if (Go.IsValidDigest(manifests[0]["digest"].S))
            load.IndexedManifestDigest = manifests[0]["digest"].S;
        var candidates = new List<J>();
        // Running total of declared blob sizes, to bound the total data the index can reference.
        long indexed = 0;
        for (var i = 0; i < manifests.Count; i++)
        {
            var descriptor = manifests[i];
            if (ValidateDescriptorFields(descriptor) is { } fieldError)
                return $"index.json manifest descriptor {i}: {fieldError}";
            // Written as "size > max - indexed" so the check itself cannot overflow.
            if (descriptor["size"].I > MaxIndexedBlobBytes - indexed)
                return $"index.json references more than {MaxIndexedBlobBytes} bytes";
            indexed += descriptor["size"].I;
            // Every referenced blob must exist and match its digest and size.
            if (OpenBlob(fs, descriptor, out _) is { } openError)
                return $"index.json manifest descriptor {i}: {openError}";
            // A descriptor is the AgentSuite manifest if it is an OCI image manifest whose
            // artifactType is empty or the AgentSuite artifact type.
            var artifactType = descriptor["artifactType"].S;
            if (descriptor["mediaType"].S == AgentSuiteTypes.OciManifestMediaType &&
                (artifactType.Length == 0 || artifactType == AgentSuiteTypes.MediaTypeArtifact))
                candidates.Add(descriptor);
        }
        if (candidates.Count != 1)
            return $"index.json must contain exactly one AgentSuite manifest descriptor, found {candidates.Count}";
        // Step 3: read and check the manifest JSON blob.
        var candidate = candidates[0];
        if (ReadBlob(fs, candidate, out var manifestBytes) is { } manifestReadError)
            return $"read AgentSuite manifest: {manifestReadError}";
        load.Manifest = new OciBlob(candidate["digest"].S, candidate["mediaType"].S, manifestBytes);
        if (AgentSuiteJson.Unmarshal(manifestBytes, AgentSuiteTypes.OciManifest, out var manifestNode) is { } manifestError)
            return $"AgentSuite OCI manifest: {manifestError}";
        var manifest = new J(manifestNode);
        var manifestMediaType = manifest["mediaType"].S;
        if (manifest["schemaVersion"].I != 2 ||
            manifestMediaType.Length != 0 && manifestMediaType != AgentSuiteTypes.OciManifestMediaType ||
            manifest["artifactType"].S != AgentSuiteTypes.MediaTypeArtifact)
            return "OCI manifest is not an AgentSuite artifact";
        // "subject" links an artifact to another image; suites must stand alone.
        if (!manifest["subject"].IsNull)
            return "AgentSuite manifest must not use subject to bind a derived sandbox image";
        // The config must be the OCI "empty" config type, and there must be one content layer.
        var config = manifest["config"];
        if (config["mediaType"].S != AgentSuiteTypes.MediaTypeEmptyConfig)
            return $"AgentSuite config mediaType must be {AgentSuiteTypes.MediaTypeEmptyConfig}";
        var layers = manifest["layers"].A;
        if (layers.Count != 1 || layers[0]["mediaType"].S != AgentSuiteTypes.MediaTypeContent)
            return $"AgentSuite manifest must contain exactly one {AgentSuiteTypes.MediaTypeContent} layer";
        if (ValidateDescriptorFields(config) is { } configFieldError)
            return $"AgentSuite config descriptor: {configFieldError}";
        if (ValidateDescriptorFields(layers[0]) is { } layerFieldError)
            return $"AgentSuite content descriptor: {layerFieldError}";
        // Step 4: the config blob must be exactly the two bytes "{}" (no whitespace).
        if (ReadBlob(fs, config, out var configBytes) is { } configReadError)
            return $"read AgentSuite config: {configReadError}";
        if (!configBytes.AsSpan().SequenceEqual("{}"u8))
            return "AgentSuite config blob must be the exact bytes {}";
        load.Config = new OciBlob(config["digest"].S, config["mediaType"].S, configBytes);
        // Step 5: open the content layer (not JSON, so OpenBlob is used instead of the JSON-limited ReadBlob)
        // and parse it as the AgentSuite gzip+tar content format.
        if (OpenBlob(fs, layers[0], out var layerBytes) is { } layerOpenError)
            return $"open AgentSuite content: {layerOpenError}";
        load.Layer = new OciBlob(layers[0]["digest"].S, layers[0]["mediaType"].S, layerBytes);
        if (AgentSuiteContent.LoadContentLayer(layerBytes, out var content) is { } contentError)
            return contentError;
        load.Content = content;
        return null;
    }

    /// <summary>Upstream <c>openOCIRegularFile</c> + <c>readRegularFile</c>.</summary>
    private static string? ReadRegularFile(LayoutFs fs, string name, long limit, out byte[] data)
    {
        data = [];
        if (OpenRegularFile(fs, name, out var bytes) is { } error)
            return error;
        if (bytes.LongLength > limit)
            return $"file must be regular and at most {limit} bytes";
        data = bytes;
        return null;
    }

    private static string? OpenRegularFile(LayoutFs fs, string name, out byte[] data)
    {
        data = [];
        if (fs.Files.TryGetValue(name, out var bytes))
        {
            data = bytes;
            return null;
        }
        // Not a file: report "wrong type" if it is a directory, otherwise Go's "not found" text.
        return fs.Dirs.Contains(name)
            ? "OCI entry must be a regular file, not a symlink or special file"
            : LayoutFs.NotFound(name);
    }

    /// <summary>Upstream <c>readBlob</c>.</summary>
    private static string? ReadBlob(LayoutFs fs, J descriptor, out byte[] data)
    {
        data = [];
        // JSON blobs (index, manifest, config) have a lower size cap than content layers.
        if (descriptor["size"].I > AgentSuiteJson.MaxBytes)
            return $"JSON blob exceeds {AgentSuiteJson.MaxBytes} bytes";
        if (OpenBlob(fs, descriptor, out var bytes) is { } error)
            return error;
        // OpenBlob already checks these; repeated here to mirror the upstream readBlob checks.
        if (bytes.LongLength != descriptor["size"].I)
            return "blob size does not match descriptor";
        if (Go.DigestBytes(bytes) != descriptor["digest"].S)
            return "blob digest does not match descriptor";
        data = bytes;
        return null;
    }

    /// <summary>Upstream <c>openBlob</c>; returns the full blob bytes.</summary>
    private static string? OpenBlob(LayoutFs fs, J descriptor, out byte[] data)
    {
        data = [];
        if (ValidateDescriptorFields(descriptor) is { } fieldError)
            return fieldError;
        // A descriptor may carry an inline copy of the blob in its "data" field.
        if (EmbeddedData(descriptor, out var embedded, out var hasEmbedded) is { } embeddedError)
            return embeddedError;
        var size = descriptor["size"].I;
        var digest = descriptor["digest"].S;
        if (hasEmbedded && (embedded.LongLength != size || Go.DigestBytes(embedded) != digest))
            return "descriptor data does not match digest and size";
        // "blobs" and "blobs/sha256" must be real directories (checked in order, outermost first).
        foreach (var directory in new[] { "blobs", "blobs/sha256" })
        {
            if (fs.Dirs.Contains(directory))
                continue;
            if (fs.Files.ContainsKey(directory))
                return "OCI blob storage must use directories, not symlinks or special files";
            return LayoutFs.NotFound(directory);
        }
        // Blobs are stored by content address: "sha256:<hex>" lives at "blobs/sha256/<hex>".
        var path = "blobs/sha256/" + (digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : digest);
        if (OpenRegularFile(fs, path, out var bytes) is { } openError)
            return openError;
        // Recompute size and SHA-256 so a blob cannot differ from what the descriptor promises.
        if (bytes.LongLength != size)
            return "blob is not a regular file of the descriptor size";
        if (Go.DigestBytes(bytes) != digest)
            return "blob digest does not match descriptor";
        // An inline copy, if present, must equal the stored blob byte for byte.
        if (hasEmbedded && !embedded.AsSpan().SequenceEqual(bytes))
            return "descriptor data does not match blob";
        data = bytes;
        return null;
    }

    /// <summary>Upstream <c>validateDescriptorFields</c>.</summary>
    private static string? ValidateDescriptorFields(J descriptor)
    {
        if (!Go.IsValidDigest(descriptor["digest"].S) || descriptor["size"].I < 0)
            return "descriptor digest or size is invalid";
        // External download URLs are refused: everything must be inside the uploaded layout.
        if (descriptor["urls"].Present)
            return "descriptor urls are not allowed in a self-contained AgentSuite layout";
        if (EmbeddedData(descriptor, out var embedded, out var hasEmbedded) is { } embeddedError)
            return embeddedError;
        if (hasEmbedded && (embedded.LongLength != descriptor["size"].I || Go.DigestBytes(embedded) != descriptor["digest"].S))
            return "descriptor data does not match digest and size";
        return null;
    }

    /// <summary>Upstream <c>ociDescriptor.embeddedData</c> (encoding/json into <c>[]byte</c>).</summary>
    private static string? EmbeddedData(J descriptor, out byte[] data, out bool present)
    {
        data = [];
        present = false;
        // Missing "data" field: nothing embedded, not an error.
        var node = descriptor["data"].Node;
        if (node is null)
            return null;
        if (node.IsNull)
            return "descriptor data must be a base64 string";
        const string prefix = "descriptor data must be a base64 string: ";
        // Go decodes JSON into a byte slice from either a base64 string or an array of numbers 0-255.
        // Both forms are accepted here, with the same error texts Go would produce.
        switch (node.Kind)
        {
            case JsonKind.String:
                if (GoBase64(node.Text!, out data) is { } base64Error)
                    return prefix + base64Error;
                break;
            case JsonKind.Array:
            {
                var bytes = new List<byte>();
                foreach (var item in node.Items!)
                {
                    // Go leaves a null array element as its zero value, i.e. byte 0.
                    if (item.Kind == JsonKind.Null)
                    {
                        bytes.Add(0);
                        continue;
                    }
                    // Anything that is not an integer 0-255 fails, like Go's uint8 decoding.
                    if (item.Kind != JsonKind.Number || !byte.TryParse(item.Text, out var value))
                    {
                        var shown = item.Kind == JsonKind.Number ? "number " + item.Text : item.GoValueName;
                        return $"{prefix}json: cannot unmarshal {shown} into Go value of type uint8";
                    }
                    bytes.Add(value);
                }
                data = [.. bytes];
                break;
            }
            default:
                return $"{prefix}json: cannot unmarshal {node.GoValueName} into Go value of type []uint8";
        }
        present = true;
        return null;
    }

    /// <summary>Go <c>base64.StdEncoding.Decode</c> (padded, newline-tolerant); returns the CorruptInputError text.</summary>
    private static string? GoBase64(string text, out byte[] data)
    {
        data = [];
        // Work on raw bytes so error positions are byte offsets, as in Go.
        var input = Encoding.UTF8.GetBytes(text);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var output = new List<byte>();
        // Base64 decodes in groups ("quanta") of 4 characters, each worth 6 bits -> 3 bytes.
        var quantum = new int[4];
        var count = 0;
        var position = 0;
        while (position < input.Length)
        {
            var c = input[position];
            // Go's decoder silently skips CR and LF anywhere in the input.
            if (c is (byte)'\r' or (byte)'\n')
            {
                position++;
                continue;
            }
            if (c == '=')
            {
                // Padding is valid only in the third or fourth quantum position and ends the input.
                if (count < 2)
                    return $"illegal base64 data at input byte {position}";
                var start = position;
                position++;
                // Two characters so far means the group needs "==" (two padding characters).
                if (count == 2)
                {
                    while (position < input.Length && input[position] is (byte)'\r' or (byte)'\n')
                        position++;
                    if (position >= input.Length || input[position] != '=')
                        return $"illegal base64 data at input byte {(position >= input.Length ? input.Length : start)}";
                    position++;
                }
                // Only newlines may follow the padding.
                while (position < input.Length && input[position] is (byte)'\r' or (byte)'\n')
                    position++;
                if (position < input.Length)
                    return $"illegal base64 data at input byte {position}";
                // Emit the 1 byte (from 2 chars) or 2 bytes (from 3 chars) of the final partial group.
                output.Add((byte)(quantum[0] << 2 | quantum[1] >> 4));
                if (count == 3)
                    output.Add((byte)(quantum[1] << 4 | quantum[2] >> 2));
                count = 0;
                break;
            }
            var value = alphabet.IndexOf((char)c);
            if (value < 0)
                return $"illegal base64 data at input byte {position}";
            quantum[count++] = value;
            position++;
            // A full group of 4 characters (24 bits) becomes 3 bytes.
            if (count == 4)
            {
                output.Add((byte)(quantum[0] << 2 | quantum[1] >> 4));
                output.Add((byte)(quantum[1] << 4 | quantum[2] >> 2));
                output.Add((byte)(quantum[2] << 6 | quantum[3]));
                count = 0;
            }
        }
        // Standard base64 must be padded, so a leftover partial group is an error.
        // The reported offset follows Go's rules for where the bad input starts.
        if (count != 0)
            return $"illegal base64 data at input byte {input.Length - (input.Length > 0 && count == 1 ? 1 : 0)}";
        data = [.. output];
        return null;
    }
}
