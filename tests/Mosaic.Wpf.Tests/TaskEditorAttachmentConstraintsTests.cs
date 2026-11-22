using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Wpf.Tests;

public sealed class TaskEditorAttachmentConstraintsTests
{
    [Fact]
    public Task ApplyConstraints_refreshes_attachment_limit_label() =>
        Sta.Run(() =>
        {
            var editor = new TaskEditorViewModel();
            var raised = new List<string>();
            editor.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

            editor.ApplyConstraints(new TaskConstraintsDto(200, 4000, true, false, 1, 25, 5));

            Assert.Contains(nameof(TaskEditorViewModel.AttachmentLimitLabel), raised);
            Assert.Contains("25", editor.AttachmentLimitLabel);
            Assert.Contains("5", editor.AttachmentLimitLabel);
            Assert.Equal(25, editor.Constraints.AttachmentMaxFileMegabytes);
            Assert.Equal(5, editor.Constraints.AttachmentMaxPerTask);
            return Task.CompletedTask;
        });
}
