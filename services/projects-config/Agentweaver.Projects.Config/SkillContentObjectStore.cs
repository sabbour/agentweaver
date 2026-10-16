using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public sealed record SkillContentObjectReference(ObjectKey Key, string ContentDigest, long StoredLength);

public sealed class SkillContentObjectStoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public const string UnavailableCode = "skill_content_unavailable";
    public const string IntegrityErrorCode = "skill_content_integrity_error";
}

public sealed class SkillContentObjectStore(IObjectStore objectStore)
{
    private const int BundleSchemaVersion = 1;
    private const int MaximumBundleBytes = 3 * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8 };

    public async Task<SkillContentObjectReference> WriteAsync(
        ObjectKey key,
        ValidatedSkillContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        var verified = Revalidate(content);
        var bundle = Serialize(verified);
        if (bundle.Length > MaximumBundleBytes)
            throw IntegrityError("The validated skill bundle exceeds the storage limit.");

        var existing = await objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            using (existing)
            {
                var bytes = await ReadBoundedAsync(existing, cancellationToken).ConfigureAwait(false);
                _ = DeserializeAndValidate(bytes, verified.ContentDigest);
                return new SkillContentObjectReference(key, verified.ContentDigest, bytes.Length);
            }
        }

        using var stream = new MemoryStream(bundle, writable: false);
        await objectStore.WriteAsync(key, stream, cancellationToken).ConfigureAwait(false);
        return new SkillContentObjectReference(key, verified.ContentDigest, bundle.Length);
    }

    public async Task<ValidatedSkillContent> ReadVerifiedAsync(
        ObjectKey key,
        string expectedContentDigest,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(expectedContentDigest))
            throw IntegrityError("The expected skill content digest is invalid.");
        var stored = await objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new SkillContentObjectStoreException(
                SkillContentObjectStoreException.UnavailableCode,
                "The immutable skill content object is unavailable.");
        using (stored)
        {
            var bytes = await ReadBoundedAsync(stored, cancellationToken).ConfigureAwait(false);
            return DeserializeAndValidate(bytes, expectedContentDigest);
        }
    }

    private static byte[] Serialize(ValidatedSkillContent content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", BundleSchemaVersion);
            writer.WriteString("name", content.Name);
            writer.WriteString("description", content.Description);
            writer.WriteString("instructions", content.Instructions);
            writer.WriteStartArray("resources");
            foreach (var resource in content.Resources)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", resource.RelativePath);
                writer.WriteBase64String("content", resource.Content.AsSpan());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static ValidatedSkillContent DeserializeAndValidate(byte[] bytes, string expectedContentDigest)
    {
        if (bytes.Length > MaximumBundleBytes)
            throw IntegrityError("The immutable skill content object exceeds the storage limit.");

        try
        {
            using var document = JsonDocument.Parse(bytes, JsonOptions);
            var root = document.RootElement;
            RequireFields(root, "schemaVersion", "name", "description", "instructions", "resources");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) ||
                schemaVersion != BundleSchemaVersion)
                throw IntegrityError("The immutable skill content schema is unsupported.");

            var name = RequireString(root.GetProperty("name"), "name");
            var description = RequireString(root.GetProperty("description"), "description");
            var instructions = RequireString(root.GetProperty("instructions"), "instructions");
            var resourceElements = root.GetProperty("resources");
            if (resourceElements.ValueKind != JsonValueKind.Array ||
                resourceElements.GetArrayLength() > SkillContentValidator.MaxResourceCount)
                throw IntegrityError("The immutable skill resource list is invalid.");

            var resources = ImmutableArray.CreateBuilder<SkillContentResourceInput>(resourceElements.GetArrayLength());
            foreach (var element in resourceElements.EnumerateArray())
            {
                RequireFields(element, "relativePath", "content");
                var path = RequireString(element.GetProperty("relativePath"), "relativePath");
                if (element.GetProperty("content").ValueKind != JsonValueKind.String)
                    throw IntegrityError("An immutable skill resource does not contain base64 text.");
                resources.Add(new SkillContentResourceInput(path, element.GetProperty("content").GetBytesFromBase64()));
            }

            var markdown = Encoding.UTF8.GetBytes(
                $"---\nname: {JsonSerializer.Serialize(name)}\ndescription: {JsonSerializer.Serialize(description)}\n---\n{instructions}\n");
            var validated = SkillContentValidator.Validate(markdown, resources.ToImmutable());
            if (!string.Equals(validated.ContentDigest, expectedContentDigest, StringComparison.Ordinal))
                throw IntegrityError("The immutable skill content digest does not match its expected revision.");
            return validated;
        }
        catch (JsonException exception)
        {
            throw IntegrityError($"The immutable skill content object is not valid JSON: {exception.Message}");
        }
        catch (FormatException exception)
        {
            throw IntegrityError($"The immutable skill resource encoding is invalid: {exception.Message}");
        }
        catch (SkillContentValidationException exception)
        {
            throw IntegrityError($"The immutable skill content failed validation: {exception.Message}");
        }
    }

    private static void RequireFields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw IntegrityError("An immutable skill content record is not a JSON object.");

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal))
                throw IntegrityError("An immutable skill content record has duplicate or unknown fields.");
        }
        if (fields.Count != expected.Length)
            throw IntegrityError("An immutable skill content record is incomplete.");
    }

    private static string RequireString(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? throw IntegrityError($"Skill content field '{field}' is null.")
            : throw IntegrityError($"Skill content field '{field}' must be a string.");

    private static async Task<byte[]> ReadBoundedAsync(ObjectRead objectRead, CancellationToken cancellationToken)
    {
        if (objectRead.Length > MaximumBundleBytes)
            throw IntegrityError("The immutable skill content object exceeds the storage limit.");

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await objectRead.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (output.Length + count > MaximumBundleBytes)
                throw IntegrityError("The immutable skill content object exceeds the storage limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (output.Length != objectRead.Length)
            throw IntegrityError("The immutable skill content object length is inconsistent.");
        return output.ToArray();
    }

    private static ValidatedSkillContent Revalidate(ValidatedSkillContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Resources.IsDefault)
            throw IntegrityError("The validated skill resource collection is missing.");

        var markdown = Encoding.UTF8.GetBytes(
            $"---\nname: {JsonSerializer.Serialize(content.Name)}\ndescription: {JsonSerializer.Serialize(content.Description)}\n---\n{content.Instructions}\n");
        var validated = SkillContentValidator.Validate(
            markdown,
            content.Resources.Select(resource =>
                new SkillContentResourceInput(resource.RelativePath, resource.Content.ToArray())).ToArray());
        if (!string.Equals(validated.ContentDigest, content.ContentDigest, StringComparison.Ordinal))
            throw IntegrityError("The validated skill content digest is inconsistent.");
        return validated;
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));

    private static SkillContentObjectStoreException IntegrityError(string message) =>
        new(SkillContentObjectStoreException.IntegrityErrorCode, message);
}
