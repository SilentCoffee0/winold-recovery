using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Restore;

public sealed record DestinationConflictPreview(int DestinationFiles, int Differing, bool Truncated)
{
    public const int MaxFiles = 5000;

    public static DestinationConflictPreview Scan(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (File.Exists(destinationPath) && !Directory.Exists(destinationPath))
        {
            if (!File.Exists(sourcePath) || IsReparse(destinationPath))
            {
                return new DestinationConflictPreview(0, 0, false);
            }

            bool same = CopyEngine.LooksLikeSuccessfulCopy(sourcePath, destinationPath);
            return new DestinationConflictPreview(1, same ? 0 : 1, false);
        }

        if (!Directory.Exists(destinationPath) || !Directory.Exists(sourcePath))
        {
            return new DestinationConflictPreview(0, 0, false);
        }

        int existing = 0;
        int differing = 0;
        bool truncated = false;
        foreach (string destFile in EnumerateDestinationFiles(destinationPath))
        {
            string relative = Path.GetRelativePath(destinationPath, destFile);
            string sourceFile = Path.Combine(sourcePath, relative);
            if (!File.Exists(sourceFile))
            {
                continue;
            }

            existing++;
            if (!CopyEngine.LooksLikeSuccessfulCopy(sourceFile, destFile))
            {
                differing++;
            }

            if (existing >= MaxFiles)
            {
                truncated = true;
                break;
            }
        }

        return new DestinationConflictPreview(existing, differing, truncated);
    }

    public static string Format(DestinationConflictPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.DestinationFiles == 0)
        {
            return "Destination does not already have these files.";
        }

        string text =
            "Destination already has " +
            QuantityFormat.Count(preview.DestinationFiles) +
            " files";
        if (preview.Differing > 0)
        {
            text += "; " + QuantityFormat.Count(preview.Differing) + " differ";
        }

        if (preview.Truncated)
        {
            text += " (first " + QuantityFormat.Count(MaxFiles) + " checked)";
        }

        return text + ".";
    }

    private static IEnumerable<string> EnumerateDestinationFiles(string root)
    {
        Stack<string> directories = new();
        directories.Push(root);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", options);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static bool IsReparse(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
