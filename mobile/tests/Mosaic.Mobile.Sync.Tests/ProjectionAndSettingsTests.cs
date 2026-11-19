using System.Text.Json;
using Mosaic.Mobile.Storage;
using Mosaic.Mobile.Sync;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Sync.Tests;

public sealed class ProjectionAndSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static ChangeDto TaskChange(
        string id,
        string title,
        long serverRevision = 1,
        ChangeOperation operation = ChangeOperation.Upsert) => new()
    {
        ServerRevision = serverRevision,
        Kind = "task",
        Key = id,
        Operation = operation == ChangeOperation.Delete ? "delete" : "upsert",
        Payload = JsonSerializer.SerializeToElement(
            new TaskItemDto
            {
                Id = Guid.Parse(id),
                Title = title,
                Notes = "note",
                CreatedAtUtc = Now.AddHours(-1),
                UpdatedAtUtc = Now,
                Revision = serverRevision,
            },
            WireJson.Options),
        UpdatedAtUtc = Now,
        DeviceId = "device-1",
    };

    [Fact]
    public void Task_upsert_projects_all_archived_fields()
    {
        var change = TaskChange(Ids.One, "Title") with
        {
            Payload = JsonSerializer.SerializeToElement(
                new TaskItemDto
                {
                    Id = Guid.Parse(Ids.One),
                    Title = "Title",
                    Notes = "notes",
                    IsCompleted = true,
                    CreatedAtUtc = Now.AddDays(-1),
                    UpdatedAtUtc = Now,
                    Revision = 7,
                    CompletedAtUtc = Now.AddMinutes(-5),
                    ArchivedAtUtc = Now,
                    RestoredAtUtc = null,
                },
                WireJson.Options),
        };

        var result = ChangeProjector.Project(
            new SyncChangesResponseDto { Changes = [change], NextCursor = "c1" },
            Now);

        var task = Assert.Single(result.Page.Tasks);
        Assert.Equal("Title", task.Title);
        Assert.True(task.IsCompleted);
        Assert.Equal(Now.AddMinutes(-5), task.CompletedAtUtc);
        Assert.Equal(Now, task.ArchivedAtUtc);
        Assert.Equal(7, task.Revision);
        Assert.Equal("c1", result.Page.NextCursor);
        Assert.Equal(1, result.ChangeCount);
    }

    [Fact]
    public void Tombstone_projects_without_payload()
    {
        var change = TaskChange(Ids.One, "x", 3, ChangeOperation.Delete);

        var result = ChangeProjector.Project(new SyncChangesResponseDto { Changes = [change] }, Now);

        var tombstone = Assert.Single(result.Page.Tombstones);
        Assert.Equal(Guid.Parse(Ids.One), tombstone.Id);
        Assert.Equal(3, tombstone.ServerRevision);
        Assert.Empty(result.Page.Tasks);
    }

    [Fact]
    public void Unknown_kind_is_ignored_with_diagnostic()
    {
        var change = TaskChange(Ids.One, "x") with { Kind = "attachment" };

        var result = ChangeProjector.Project(new SyncChangesResponseDto { Changes = [change] }, Now);

        Assert.Empty(result.Page.Tasks);
        Assert.Contains(result.Diagnostics, d => d.Contains("kind_unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_task_key_and_payload_are_skipped()
    {
        var badKey = TaskChange(Ids.One, "x") with { Key = "not-a-guid" };
        var badPayload = TaskChange(Ids.Two, "x") with { Payload = JsonSerializer.SerializeToElement("text") };

        var result = ChangeProjector.Project(
            new SyncChangesResponseDto { Changes = [badKey, badPayload] },
            Now);

        Assert.Empty(result.Page.Tasks);
        Assert.Contains(result.Diagnostics, d => d == "task_key_invalid");
        Assert.Contains(result.Diagnostics, d => d == "task_payload_missing");
    }

    [Fact]
    public void Unknown_setting_is_ignored_but_known_setting_is_kept()
    {
        var known = new ChangeDto
        {
            ServerRevision = 2,
            Kind = "setting",
            Key = "ui.language",
            Operation = "upsert",
            Payload = JsonSerializer.SerializeToElement(new { value = "English" }),
        };
        var unknown = known with { ServerRevision = 3, Key = "shell.window.width" };

        var result = ChangeProjector.Project(
            new SyncChangesResponseDto { Changes = [known, unknown] },
            Now);

        var setting = Assert.Single(result.Page.Settings);
        Assert.Equal("ui.language", setting.Key);
        Assert.Equal("\"English\"", setting.ValueJson);
        Assert.Contains(result.Diagnostics, d => d.Contains("setting_unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Setting_delete_projects_null_value()
    {
        var change = new ChangeDto
        {
            ServerRevision = 4,
            Kind = "setting",
            Key = "appearance.theme",
            Operation = "delete",
        };

        var result = ChangeProjector.Project(new SyncChangesResponseDto { Changes = [change] }, Now);

        var setting = Assert.Single(result.Page.Settings);
        Assert.Equal("null", setting.ValueJson);
    }

    [Fact]
    public void Settings_apply_whitelisted_values()
    {
        var settings = new PortableSettings();

        settings.Apply("ui.language", JsonSerializer.SerializeToElement("English"));
        settings.Apply("appearance.theme", JsonSerializer.SerializeToElement("Dark"));
        settings.Apply("tasks.sortOrder", JsonSerializer.SerializeToElement("TitleAsc"));
        settings.Apply("tasks.archive.delayDays", JsonSerializer.SerializeToElement(30));
        settings.Apply("tasks.confirmDelete", JsonSerializer.SerializeToElement(false));

        Assert.Equal(AppLanguagePreference.English, settings.Language);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(TaskSortPreference.TitleAsc, settings.SortOrder);
        Assert.Equal(30, settings.ArchiveDelayDays);
        Assert.False(settings.ConfirmDelete);
    }

    [Fact]
    public void High_contrast_is_downgraded_and_reported()
    {
        var settings = new PortableSettings();

        settings.Apply("appearance.theme", JsonSerializer.SerializeToElement("HighContrast"));

        Assert.Equal(ThemePreference.FollowSystem, settings.Theme);
        Assert.True(settings.HighContrastDowngraded);
    }

    [Fact]
    public void Invalid_values_keep_the_previous_value_and_add_diagnostics()
    {
        var settings = new PortableSettings();
        settings.Apply("tasks.sortOrder", JsonSerializer.SerializeToElement("TitleAsc"));

        settings.Apply("tasks.sortOrder", JsonSerializer.SerializeToElement("Sideways"));
        settings.Apply("tasks.title.maxCharacters", JsonSerializer.SerializeToElement(0));
        settings.Apply("tasks.confirmDelete", JsonSerializer.SerializeToElement("yes"));
        settings.Apply("shell.window.width", JsonSerializer.SerializeToElement(10));

        Assert.Equal(TaskSortPreference.TitleAsc, settings.SortOrder);
        Assert.Equal(200, settings.TitleMaxCharacters);
        Assert.True(settings.ConfirmDelete);
        Assert.Contains(settings.Diagnostics, d => d.Key == "tasks.sortOrder" && d.Reason == "value");
        Assert.Contains(settings.Diagnostics, d => d.Key == "tasks.title.maxCharacters" && d.Reason == "range");
        Assert.Contains(settings.Diagnostics, d => d.Key == "tasks.confirmDelete" && d.Reason == "type");
        Assert.Contains(settings.Diagnostics, d => d.Key == "shell.window.width" && d.Reason == "unknown_key");
    }

    [Fact]
    public void Null_resets_a_key_to_its_default()
    {
        var settings = new PortableSettings();
        settings.Apply("appearance.theme", JsonSerializer.SerializeToElement("Dark"));
        settings.Apply("tasks.archive.delayDays", JsonSerializer.SerializeToElement(30));

        settings.Apply("appearance.theme", JsonSerializer.SerializeToElement<object?>(null));
        settings.Apply("tasks.archive.delayDays", JsonSerializer.SerializeToElement<object?>(null));

        Assert.Equal(ThemePreference.FollowSystem, settings.Theme);
        Assert.Equal(7, settings.ArchiveDelayDays);
    }

    [Fact]
    public void Apply_all_uses_server_revision_order()
    {
        var settings = new PortableSettings();
        var values = new[]
        {
            new StoredSetting("appearance.theme", "\"Dark\"", 5, Now),
            new StoredSetting("appearance.theme", "\"Light\"", 2, Now),
        };

        settings.ApplyAll(values);

        Assert.Equal(ThemePreference.Dark, settings.Theme);
    }

    [Fact]
    public void Reset_all_returns_defaults()
    {
        var settings = new PortableSettings();
        settings.Apply("ui.language", JsonSerializer.SerializeToElement("English"));
        settings.Apply("appearance.theme", JsonSerializer.SerializeToElement("Dark"));

        settings.ResetAll();

        Assert.Equal(AppLanguagePreference.FollowSystem, settings.Language);
        Assert.Equal(ThemePreference.FollowSystem, settings.Theme);
    }
}
