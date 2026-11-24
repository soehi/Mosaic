namespace Mosaic.Server.Attachments;

/// <summary>
/// Disk layout for attachment blobs: every user gets one directory and every
/// attachment one file named after its normalized id plus a sanitized extension.
/// Both the API and account deletion go through here so paths can never escape
/// the data directory from client-supplied values.
/// </summary>
internal static class AttachmentPaths
{
    public const int MaxFileNameCharacters = 260;

    public static string Root(ServerOptions options) =>
        Path.Combine(options.DataDirectory, "attachments");

    public static string UserDirectory(ServerOptions options, string userId) =>
        Path.Combine(Root(options), NormalizeUserId(userId));

    public static string FileFor(ServerOptions options, string userId, string attachmentId, string fileName) =>
        Path.Combine(UserDirectory(options, userId), NormalizeAttachmentId(attachmentId) + Extension(fileName));

    /// <summary>Returns the lowercase 32-character form used on disk.</summary>
    public static string NormalizeUserId(string userId)
    {
        if (!Guid.TryParse(userId, out var parsed))
        {
            throw new InvalidOperationException("用户标识不是有效的 Guid，无法定位附件目录。");
        }

        return parsed.ToString("N");
    }

    /// <summary>Returns the lowercase 32-character form used on disk.</summary>
    public static string NormalizeAttachmentId(string attachmentId)
    {
        if (!Guid.TryParse(attachmentId, out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidOperationException("附件标识不是有效的 Guid。");
        }

        return parsed.ToString("N");
    }

    /// <summary>
    /// Only a short alphanumeric extension is kept; anything else is dropped so a
    /// hostile file name cannot influence the path.
    /// </summary>
    public static string Extension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Length is < 2 or > 17)
        {
            return string.Empty;
        }

        foreach (var character in extension)
        {
            if (character != '.' && !char.IsLetterOrDigit(character))
            {
                return string.Empty;
            }
        }

        return extension.ToLowerInvariant();
    }

    /// <summary>
    /// Removes every blob the deleted account owned. Best effort: the database
    /// rows are already gone, so a leftover directory is only wasted disk space.
    /// </summary>
    public static bool TryDeleteUserDirectory(ServerOptions options, string userId, out string? error)
    {
        try
        {
            var directory = UserDirectory(options, userId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = exception.GetType().Name;
            return false;
        }
    }
}