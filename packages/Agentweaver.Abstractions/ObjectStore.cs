namespace Agentweaver.Abstractions;

/// <summary>An opaque platform object identity, not an agent workspace path.</summary>
public readonly record struct ObjectKey
{
    /// <summary>The validated relative, slash-separated object identity.</summary>
    public string Value { get; }

    /// <summary>Creates an object key and rejects absolute, empty, or traversal-like segments.</summary>
    public ObjectKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1024 || value.StartsWith('/') || value.EndsWith('/') ||
            value.Contains('\\') || value.Any(char.IsControl) ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Object keys must be nonempty, relative slash-separated segments.", nameof(value));
        Value = value;
    }

    /// <summary>Returns the object key's validated value.</summary>
    public override string ToString() => Value;
}

/// <summary>The caller owns the input stream; the caller must dispose the returned download.</summary>
public interface IObjectStore
{
    /// <summary>Creates an object only if its key is absent. Never overwrites existing content.</summary>
    Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Returns null for a missing object. Dispose the returned download to release its transport.</summary>
    Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default);

    /// <summary>Returns false only when the object does not exist.</summary>
    Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns a downloaded object's stream and transport cleanup callback; dispose it after
/// consuming the content so the provider can release its resources.
/// </summary>
public sealed class ObjectRead : IDisposable
{
    private readonly Action _dispose;

    /// <summary>Creates a download wrapper with its content length and cleanup action.</summary>
    /// <exception cref="ArgumentNullException">The stream or cleanup action is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The content length is negative.</exception>
    public ObjectRead(Stream content, long length, Action dispose)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        Length = length;
    }

    /// <summary>The readable object stream, valid until this wrapper is disposed.</summary>
    public Stream Content { get; }
    /// <summary>The number of bytes in the object.</summary>
    public long Length { get; }

    /// <summary>Releases the provider transport associated with the read.</summary>
    public void Dispose() => _dispose();
}
