namespace Mosaic.Mobile.Core.Auth;

/// <summary>
/// Platform-protected byte storage (Android Keystore on the host). A failure
/// here is a session-unavailable condition, never a crash: the card renders
/// "会话不可用" and stops using the projection.
/// </summary>
public interface ISecretStorage
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);

    Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed class SecretStorageException : Exception
{
    public SecretStorageException(string operation, Exception inner)
        : base($"Secure storage operation '{operation}' failed.", inner)
    {
        Operation = operation;
    }

    public string Operation { get; }
}

/// <summary>Process-local storage used by tests and by platforms without a keystore.</summary>
public sealed class InMemorySecretStorage : ISecretStorage
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        lock (_values)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }
    }

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_values)
        {
            _values[key] = value.ToArray();
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        lock (_values)
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}