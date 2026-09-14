using System.Xml;
using System.Xml.Linq;

namespace WinOldRecovery.Recipes;

public static class SyncthingConfig
{
    public static XDocument Load(string xml)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
        };
        using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static string Rewrite(
        string xml,
        string oldProfileRoot,
        string newProfileRoot,
        IReadOnlyDictionary<string, string>? pathByFolderId = null)
    {
        XDocument document = Load(xml);
        foreach (XElement folder in document.Descendants("folder"))
        {
            folder.SetAttributeValue("paused", "true");
            string? path = (string?)folder.Attribute("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string remapped = RemapPath(path, oldProfileRoot, newProfileRoot);
            string? id = (string?)folder.Attribute("id");
            if (!string.IsNullOrWhiteSpace(id) &&
                pathByFolderId is not null &&
                pathByFolderId.TryGetValue(id, out string? overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                remapped = overridePath;
            }

            folder.SetAttributeValue("path", remapped);
        }

        foreach (XElement parameter in document.Descendants("param"))
        {
            if (!string.Equals((string?)parameter.Attribute("name"), "versionsPath", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? path = (string?)parameter.Attribute("val") ?? parameter.Value;
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
            {
                parameter.SetAttributeValue("val", RemapPath(path, oldProfileRoot, newProfileRoot));
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string ScrubSecrets(string xml)
    {
        XDocument document = Load(xml);
        foreach (string name in new[] { "apikey", "password", "user" })
        {
            foreach (XElement element in document.Descendants(name))
            {
                if (!string.IsNullOrEmpty(element.Value))
                {
                    element.Value = "***";
                }
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<SyncthingFolder> ListFolders(string xml)
    {
        XDocument document = Load(xml);
        List<SyncthingFolder> folders = [];
        foreach (XElement folder in document.Descendants("folder"))
        {
            string? id = (string?)folder.Attribute("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            folders.Add(
                new SyncthingFolder(
                    id,
                    (string?)folder.Attribute("label") ?? id,
                    (string?)folder.Attribute("path") ?? string.Empty));
        }

        return folders;
    }

    public static IReadOnlyList<SyncthingFolderMapping> PlanMappings(
        string xml,
        string oldProfileRoot,
        string newProfileRoot,
        IReadOnlyDictionary<string, string>? pathByFolderId = null)
    {
        List<SyncthingFolderMapping> mappings = [];
        foreach (SyncthingFolder folder in ListFolders(xml))
        {
            string planned = RemapPath(folder.Path, oldProfileRoot, newProfileRoot);
            if (pathByFolderId is not null &&
                pathByFolderId.TryGetValue(folder.Id, out string? overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                planned = overridePath;
            }

            mappings.Add(
                new SyncthingFolderMapping(
                    folder.Id,
                    folder.Label,
                    folder.Path,
                    planned,
                    Directory.Exists(planned)));
        }

        return mappings;
    }

    public static int FolderCount(XDocument document) =>
        document.Descendants("folder").Count(static folder => !string.IsNullOrWhiteSpace((string?)folder.Attribute("id")));

    public static int DeviceCount(XDocument document) =>
        document.Root?.Elements("device").Count() ?? 0;

    public static bool AllFoldersPaused(string xml)
    {
        XDocument document = Load(xml);
        return document.Descendants("folder").All(static folder =>
            string.Equals((string?)folder.Attribute("paused"), "true", StringComparison.OrdinalIgnoreCase));
    }

    public static string RemapPath(string path, string oldProfileRoot, string newProfileRoot)
    {
        string oldFull = Path.GetFullPath(oldProfileRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
        candidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidate.StartsWith(oldFull, StringComparison.OrdinalIgnoreCase))
        {
            string tail = candidate[oldFull.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(tail) ? newProfileRoot : Path.Combine(newProfileRoot, tail);
        }

        return path;
    }
}

public sealed record SyncthingFolder(string Id, string Label, string Path);

public sealed record SyncthingFolderMapping(
    string Id,
    string Label,
    string SourcePath,
    string PlannedPath,
    bool ExistsOnDestination);
