namespace Agentweaver.Abstractions;

/// <summary>An opaque platform object identity, not an agent workspace path.</summary>
public readonly record struct ObjectKey
{
    public string Value { get; }

    public ObjectKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1024 || value.StartsWith('/') || value.EndsWith('/') ||
            value.Contains('\\') || value.Any(char.IsControl) ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Object keys must be nonempty, relative slash-separated segments.", nameof(value));
        Value = value;
    }

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

public sealed class ObjectRead : IDisposable
{
    private readonly Action _dispose;

    public ObjectRead(Stream content, long length, Action dispose)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        Length = length;
    }

    public Stream Content { get; }
    public long Length { get; }
    public void Dispose() => _dispose();
}
