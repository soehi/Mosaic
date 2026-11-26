namespace Mosaic.Mobile.Core.Auth;

/// <summary>
/// What a command needs from the session: a usable access token, and a way to
/// clear the credential after the server has confirmed it is no longer valid.
/// </summary>
public interface IAccessTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task InvalidateAsync(CancellationToken cancellationToken);
}