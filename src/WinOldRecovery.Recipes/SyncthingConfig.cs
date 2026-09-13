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

    public static string Rewrite(string xml, string oldProfileRoot, string newProfileRoot)
    {
        XDocument document = Load(xml);
        foreach (XElement folder in document.Descendants("folder"))
        {
            folder.SetAttributeValue("paused", "true");
            string? path = (string?)folder.Attribute("path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                folder.SetAttributeValue("path", RemapPath(path, oldProfileRoot, newProfileRoot));
            }
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

    public static int FolderCount(XDocument document) => document.Descendants("folder").Count();

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
        string candidate = path;
        if (candidate.StartsWith(oldFull, StringComparison.OrdinalIgnoreCase))
        {
            string tail = candidate[oldFull.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(tail) ? newProfileRoot : Path.Combine(newProfileRoot, tail);
        }

        return path;
    }
}
