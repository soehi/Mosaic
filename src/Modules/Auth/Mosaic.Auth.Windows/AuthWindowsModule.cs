using Mosaic.Auth.Contracts;
using Mosaic.Contracts;

namespace Mosaic.Auth.Windows;

public sealed class AuthWindowsModule(string secretsPath) : IModuleActivation
{
    private readonly WindowsAuthSecretStore _store = new(secretsPath);
    private bool _active;

    public static ModuleManifest Definition { get; } = new(
        "auth-windows",
        [new("auth.secrets", 1)],
        [],
        "Stores account credentials with Windows DPAPI for the current user.");

    public ModuleManifest Manifest => Definition;

    public IAuthSecretStore Secrets => _active ? _store : throw new InvalidOperationException("Account secret store is not active.");

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        _active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _active = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _active = false;
        return ValueTask.CompletedTask;
    }
}
