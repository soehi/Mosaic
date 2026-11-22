using System.Windows.Threading;
using Mosaic.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Wpf.Tests;

public sealed class TaskAttachmentLocalizationTests
{
    private static readonly string[] ErrorKeys =
    [
        "Tasks.Attachments.Error.TooLarge",
        "Tasks.Attachments.Error.TooMany",
        "Tasks.Attachments.Error.ContentMissing",
        "Tasks.Attachments.Error.TargetExists",
        "Tasks.Attachments.Error.WriteFailed",
        "Tasks.Attachments.Error.NotFound",
        "Tasks.Attachments.Error.StoreUnavailable",
    ];

    [Fact]
    public Task Attachment_error_keys_exist_in_both_languages_and_localizer_never_falls_back_to_generic() =>
        Sta.Run(async () =>
        {
            using var registration = MosaicLocalizationManager.Register(
                "tasks-ui-tests",
                language => TaskLocalization.Dictionary(language == MosaicLanguage.English));
            var previous = MosaicLocalizationManager.Current;
            try
            {
                var constraints = new TaskConstraintsDto(
                    TitleMaxCharacters: 200,
                    NotesMaxCharacters: 4000,
                    TitleRequired: true,
                    DefaultCompleted: false,
                    RevisionStart: 1,
                    AttachmentMaxFileMegabytes: 50,
                    AttachmentMaxPerTask: 10);

                foreach (var language in new[] { MosaicLanguage.Chinese, MosaicLanguage.English })
                {
                    MosaicLocalizationManager.SetLanguage(language);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    foreach (var key in ErrorKeys)
                    {
                        var localized = TaskLocalization.Localize(key, "");
                        Assert.False(string.IsNullOrWhiteSpace(localized), $"{key} missing in {language}");
                    }

                    var tooLarge = TaskErrorLocalizer.Localize(
                        new OperationError(ErrorCodes.ValidationFailed, "raw", MessageKey: "Tasks.Attachments.Error.TooLarge"),
                        constraints);
                    Assert.Contains("50", tooLarge);
                    if (language == MosaicLanguage.English)
                        Assert.DoesNotContain(tooLarge, c => c is >= '一' and <= '鿿');

                    var tooMany = TaskErrorLocalizer.Localize(
                        new OperationError(ErrorCodes.ValidationFailed, "raw", MessageKey: "Tasks.Attachments.Error.TooMany"),
                        constraints);
                    Assert.Contains("10", tooMany);

                    foreach (var key in ErrorKeys.Skip(2))
                    {
                        var text = TaskErrorLocalizer.Localize(
                            new OperationError(ErrorCodes.InternalError, "raw", MessageKey: key),
                            constraints);
                        Assert.DoesNotContain("任务操作失败", text);
                        Assert.NotEqual("raw", text);
                        if (language == MosaicLanguage.English)
                            Assert.DoesNotContain(text, c => c is >= '一' and <= '鿿');
                    }
                }
            }
            finally
            {
                MosaicLocalizationManager.SetLanguage(previous);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        });
}
