using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mosaic.Desktop;

internal sealed class SingleInstance : IAsyncDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private readonly string _pipeName;
    private readonly FileStream? _lock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<Exception>? _reportFailure;
    private Task? _listener;
    private int _disposed;

    internal SingleInstance(string root, Action<Exception>? reportFailure = null)
    {
        _reportFailure = reportFailure;
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".mosaic.instance.lock");
        FileStream? owned = null;
        string canonicalPath;
        try
        {
            owned = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            canonicalPath = FinalPath(owned.SafeFileHandle, root);
        }
        catch (IOException ex) when (IsSharingConflict(ex))
        {
            using var existing = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            canonicalPath = FinalPath(existing.SafeFileHandle, root);
        }
        _lock = owned;
        _pipeName = "Mosaic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath.ToUpperInvariant())))[..32];
    }

    internal bool IsPrimary => _lock is not null;

    internal void Listen(Action activate) => _listener = ListenAsync(activate);

    private async Task ListenAsync(Action activate)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                var data = new byte[1];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (await pipe.ReadAsync(data, timeout.Token).ConfigureAwait(false) == 1 && data[0] == 1)
                {
                    try { activate(); }
                    catch (Exception ex) { Report(ex); }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Report(ex);
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), _stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task NotifyAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        if (_listener is not null)
        {
            try { await _listener; }
            catch (Exception ex) { Report(ex); }
        }
        if (_lock is not null) await _lock.DisposeAsync();
        _stopping.Dispose();
    }

    private void Report(Exception exception)
    {
        try { _reportFailure?.Invoke(exception); }
        catch { /* Listener failure reporting must not terminate the listener. */ }
    }

    private static bool IsSharingConflict(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    private static string FinalPath(SafeFileHandle handle, string fallback)
    {
        var capacity = 512;
        while (true)
        {
            var path = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(fallback));
            if (length < path.Capacity) return Path.TrimEndingDirectorySeparator(path.ToString(0, (int)length));
            capacity = checked((int)length + 1);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
