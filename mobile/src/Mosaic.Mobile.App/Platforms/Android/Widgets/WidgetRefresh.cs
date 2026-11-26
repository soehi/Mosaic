using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Presentation;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.App.Platforms.Android.Widgets;

/// <summary>
/// One refresh funnel for widget updates: provider callbacks, narrow writes,
/// sync completion and preference changes all land here. A refresh re-establishes
/// the session and checks identity before it reads or renders anything, so an
/// event from the previous account can never update a widget with stale data.
/// </summary>
public sealed class WidgetRefresh : IDisposable
{
    private readonly Context _context;
    private readonly WidgetTaskRepository _repository;
    private readonly ProjectionSession _projection;
    private readonly AuthSessionManager _auth;
    private bool _subscribed;
    private bool _disposed;

    public WidgetRefresh(
        Context context,
        WidgetTaskRepository repository,
        ProjectionSession projection,
        AuthSessionManager auth)
    {
        _context = context;
        _repository = repository;
        _projection = projection;
        _auth = auth;
    }

    public void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _auth.StateChanged += OnAuthStateChanged;
        _auth.LocalDataCleared += OnLocalDataCleared;
        _projection.Changed += OnProjectionChanged;
        _projection.Completed += OnProjectionCompleted;
        _projection.StatusChanged += OnStatusChanged;
    }

    /// <summary>Refreshes every installed instance, optionally showing a write result.</summary>
    public async Task RefreshAllAsync(string? notice = null, CancellationToken cancellationToken = default)
    {
        var manager = AppWidgetManager.GetInstance(_context);
        if (manager is null)
        {
            return;
        }

        await RefreshAsync(manager, manager.GetAppWidgetIds(WidgetComponent()), notice, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Refreshes only the supplied instances; used after a widget action.</summary>
    public async Task RefreshAsync(
        int appWidgetId,
        string? notice = null,
        CancellationToken cancellationToken = default)
    {
        var manager = AppWidgetManager.GetInstance(_context);
        if (manager is null || appWidgetId == AppWidgetManager.InvalidAppwidgetId)
        {
            return;
        }

        await RefreshAsync(manager, new[] { appWidgetId }, notice, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(
        AppWidgetManager manager,
        int[]? appWidgetIds,
        string? notice,
        CancellationToken cancellationToken)
    {
        if (appWidgetIds is null || appWidgetIds.Length == 0)
        {
            return;
        }

        // Establish fresh state on every refresh: an old in-flight broadcast or
        // event handler cannot keep using a generation captured before a switch.
        var context = await _repository.EstablishAsync(cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            DrawBlocked(manager, appWidgetIds, WidgetBlockedReason.NeedsSignIn);
            return;
        }

        foreach (var appWidgetId in appWidgetIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Bundle? options = null;
            try
            {
                options = manager.GetAppWidgetOptions(appWidgetId);
            }
            catch (Exception)
            {
                // An instance may have been removed between GetAppWidgetIds and
                // this call; render the compact layout as the safe default.
            }

            var maxRows = WidgetFormatter.MaxRows(options);
            var snapshot = await _repository.ReadAsync(context, maxRows, cancellationToken).ConfigureAwait(false);
            // A write or callback may have invalidated the generation while the
            // snapshot was being read; never publish that snapshot.
            if (!_repository.IsCurrent(context.Generation))
            {
                DrawBlocked(manager, new[] { appWidgetId }, WidgetBlockedReason.NeedsSignIn);
                continue;
            }

            var views = WidgetFormatter.Build(
                _context,
                snapshot,
                appWidgetId,
                maxRows,
                maxRows > 1,
                notice,
                _repository);
            manager.UpdateAppWidget(appWidgetId, views);
        }
    }

    private void DrawBlocked(AppWidgetManager manager, int[] appWidgetIds, WidgetBlockedReason reason)
    {
        var snapshot = WidgetSnapshot.Blocked(reason);
        foreach (var appWidgetId in appWidgetIds)
        {
            var views = WidgetFormatter.Build(
                _context,
                snapshot,
                appWidgetId,
                1,
                false,
                null,
                _repository);
            manager.UpdateAppWidget(appWidgetId, views);
        }
    }

    private static ComponentName WidgetComponent() =>
        new(global::Android.App.Application.Context.PackageName!, typeof(TaskWidgetProvider).FullName!);

    private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e) => FireAndForget();

    private void OnLocalDataCleared(object? sender, string reason) => FireAndForget();

    private void OnProjectionChanged(object? sender, EventArgs e) => FireAndForget();

    private void OnProjectionCompleted(object? sender, SyncReport report) => FireAndForget();

    private void OnStatusChanged(object? sender, SyncStatusSnapshot status) => FireAndForget();

    private void FireAndForget() => _ = Task.Run(async () =>
    {
        try
        {
            await RefreshAllAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Broadcast refresh is best effort; never surface a crash to the app.
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _auth.StateChanged -= OnAuthStateChanged;
        _auth.LocalDataCleared -= OnLocalDataCleared;
        _projection.Changed -= OnProjectionChanged;
        _projection.Completed -= OnProjectionCompleted;
        _projection.StatusChanged -= OnStatusChanged;
    }
}
