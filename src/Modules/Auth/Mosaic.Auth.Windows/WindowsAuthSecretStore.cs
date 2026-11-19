using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;

namespace Mosaic.Auth.Windows;

/// <summary>
/// Persists the refresh token and device identity protected with Windows DPAPI for the current
/// user. The file lives beside the data root, is written atomically, and is never plain text, so
/// copying it to another account or machine yields nothing usable.
/// </summary>
public sealed class WindowsAuthSecretStore : IAuthSecretStore
{
    private const int CurrentVersion = 1;
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "Mosaic.Auth.Secrets.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsAuthSecretStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("凭据文件路径不能为空。", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public async Task<OperationResult<AuthSecrets?>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return OperationResult<AuthSecrets?>.Success(null);
            byte[] protectedBytes;
            try
            {
                protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Failure<AuthSecrets?>(AuthErrorCodes.SecretsUnavailable, "无法读取本机保存的登录凭据。");
            }
            try
            {
                var payload = JsonSerializer.Deserialize<StoredSecret>(Unprotect(protectedBytes), JsonOptions);
                if (payload is null || payload.Version != CurrentVersion ||
                    string.IsNullOrWhiteSpace(payload.RefreshToken) ||
                    string.IsNullOrWhiteSpace(payload.Email) ||
                    !Uri.TryCreate(payload.Server, UriKind.Absolute, out var server) ||
                    !AuthValidation.IsTrustedServer(server))
                    return OperationResult<AuthSecrets?>.Success(null);
                return OperationResult<AuthSecrets?>.Success(new(
                    payload.RefreshToken,
                    payload.DeviceId,
                    payload.UserId,
                    payload.Email,
                    payload.DisplayName,
                    server));
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or Win32Exception)
            {
                // A file from another Windows account, another machine or a damaged write is
                // treated as absent: the user simply signs in again.
                return OperationResult<AuthSecrets?>.Success(null);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationResult> SaveAsync(AuthSecrets secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrWhiteSpace(secrets.RefreshToken) || !AuthValidation.IsTrustedServer(secrets.Server))
            return OperationResult.Failure(new(AuthErrorCodes.SecretsUnavailable, "登录凭据不完整，未写入本机。"));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = new StoredSecret(
                CurrentVersion,
                secrets.RefreshToken,
                secrets.DeviceId,
                secrets.UserId,
                secrets.Email,
                secrets.DisplayName,
                secrets.Server.AbsoluteUri);
            var protectedBytes = Protect(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return OperationResult.Failure(new(AuthErrorCodes.SecretsUnavailable, "无法保存登录凭据：" + exception.Message));
            }
            return OperationResult.Success();
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                return OperationResult.Success();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Failure(new(AuthErrorCodes.SecretsUnavailable, "无法清除本机保存的登录凭据：" + exception.Message));
            }
        }
        finally { _gate.Release(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static OperationResult<T> Failure<T>(string code, string message) =>
        OperationResult<T>.Failure(new(code, message));

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = ToBlob(data);
        var entropy = ToBlob(Entropy);
        try
        {
            DataBlob output;
            var success = protect
                ? CryptProtectData(ref input, "Mosaic account credentials", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new() { Size = data.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record StoredSecret(
        int Version,
        string RefreshToken,
        Guid DeviceId,
        Guid UserId,
        string Email,
        string? DisplayName,
        string Server);
}
