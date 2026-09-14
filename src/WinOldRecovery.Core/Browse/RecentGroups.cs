using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Browse;

public static class RecentGroups
{
    public static IReadOnlyList<TreeNodeRow> InsertHeaders(
        IReadOnlyList<TreeNodeRow> files,
        NodeBrowser browser,
        long? continueParentId = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(browser);

        List<TreeNodeRow> rows = new(files.Count + 8);
        long? lastParent = continueParentId;
        foreach (TreeNodeRow file in files)
        {
            if (file.IsGroupHeader)
            {
                continue;
            }

            if (file.ParentId != lastParent)
            {
                lastParent = file.ParentId;
                rows.Add(HeaderFor(file, browser));
            }

            rows.Add(file);
        }

        return rows;
    }

    private static TreeNodeRow HeaderFor(TreeNodeRow file, NodeBrowser browser)
    {
        if (file.ParentId is long parentId)
        {
            TreeNodeRow? folder = browser.GetNode(parentId);
            if (folder is not null)
            {
                return folder with { IsGroupHeader = true };
            }
        }

        string folderPath = ParentRelPath(file.RelPath);
        return file with
        {
            IsGroupHeader = true,
            Name = string.IsNullOrEmpty(folderPath) ? file.Name : Path.GetFileName(folderPath),
            RelPath = folderPath,
            Kind = NodeKind.Directory,
        };
    }

    private static string ParentRelPath(string relPath)
    {
        int slash = relPath.LastIndexOf('\\');
        return slash < 0 ? string.Empty : relPath[..slash];
    }
}
