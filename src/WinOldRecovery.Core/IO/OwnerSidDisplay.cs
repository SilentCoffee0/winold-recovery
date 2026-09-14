using System.ComponentModel;
using System.Security.Principal;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.IO;

public static class OwnerSidDisplay
{
    public static string Line(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Owner: unavailable";
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return "Owner: unavailable";
            }

            return LineFromSid(FileSecurityInfo.GetOwnerSid(path));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Owner: unavailable";
        }
    }

    public static string LineFromSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        try
        {
            string account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            return "Owner: " + account + " (" + sid + ")";
        }
        catch (IdentityNotMappedException)
        {
            return "Owner: " + sid + " (old account, no longer exists)";
        }
        catch (ArgumentException)
        {
            return "Owner: " + sid + " (old account, no longer exists)";
        }
        catch (SystemException)
        {
            return "Owner: " + sid + " (old account, no longer exists)";
        }
    }

    public static string AttributesLine(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Attributes: unavailable";
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return "Attributes: unavailable";
            }

            return "Attributes: " + File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Attributes: unavailable";
        }
    }
}
