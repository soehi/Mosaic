using System.IO;
using Mosaic.Maintenance.Contracts;

namespace Mosaic.Maintenance.Tests;

public sealed class UserDataRootGuardTests
{
    private static string DefaultRoot =>
        Path.TrimEndingDirectorySeparator(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\Mosaic")]
    public void Rejects_empty_or_relative_paths(string? candidate)
    {
        Assert.False(UserDataRootGuard.IsSafeToDelete(candidate));
    }

    [Fact]
    public void Rejects_drive_root()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
        Assert.False(UserDataRootGuard.IsSafeToDelete(root));
    }

    [Fact]
    public void Rejects_user_profile()
    {
        Assert.False(UserDataRootGuard.IsSafeToDelete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void Rejects_local_app_data_without_the_mosaic_folder()
    {
        Assert.False(UserDataRootGuard.IsSafeToDelete(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }

    [Fact]
    public void Rejects_paths_that_escape_the_default_root()
    {
        Assert.False(UserDataRootGuard.IsSafeToDelete(Path.Combine(DefaultRoot, "..")));
        Assert.False(UserDataRootGuard.IsSafeToDelete(Path.Combine(DefaultRoot, "..", "Other")));
    }

    [Fact]
    public void Accepts_the_default_root_with_or_without_trailing_separator()
    {
        Assert.True(UserDataRootGuard.IsSafeToDelete(DefaultRoot));
        Assert.True(UserDataRootGuard.IsSafeToDelete(DefaultRoot + Path.DirectorySeparatorChar));
        Assert.True(UserDataRootGuard.IsSafeToDelete(DefaultRoot.ToUpperInvariant()));
    }
}
