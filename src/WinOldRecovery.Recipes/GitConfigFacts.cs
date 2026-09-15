namespace WinOldRecovery.Recipes;

internal static class GitConfigFacts
{
    public static Dictionary<string, string> Read(string text)
    {
        Dictionary<string, string> facts = new(StringComparer.Ordinal);
        string section = string.Empty;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim().Trim('"');
                int space = section.IndexOf(' ');
                if (space > 0)
                {
                    section = section[..space];
                }

                continue;
            }

            int equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = trimmed[..equals].Trim();
            string value = trimmed[(equals + 1)..].Trim();
            if (section.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                facts["userName"] = value;
            }
            else if (section.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("email", StringComparison.OrdinalIgnoreCase))
            {
                facts["userEmail"] = value;
            }
            else if (section.Equals("core", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("sshCommand", StringComparison.OrdinalIgnoreCase))
            {
                facts["sshCommand"] = value;
            }
            else if (section.Equals("credential", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("helper", StringComparison.OrdinalIgnoreCase))
            {
                facts["credentialHelper"] = value;
            }
        }

        return facts;
    }
}
