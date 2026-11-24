using Xunit;

namespace Mosaic.Wpf.Tests;

// MosaicLocalizationManager keeps the active language in process-wide static state, so the tests
// that register owners or switch languages must not run beside other collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiLocalizationCollection
{
    public const string Name = "ui-localization";
}