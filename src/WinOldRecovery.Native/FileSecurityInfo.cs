using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinOldRecovery.Native;

public static class FileSecurityInfo
{
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;

    public static string GetOwnerSid(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        uint error = GetNamedSecurityInfo(
            path,
            SeFileObject,
            OwnerSecurityInformation,
            out IntPtr ownerSid,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            out IntPtr securityDescriptor);

        if (error != 0)
        {
            throw new Win32Exception((int)error, $"Could not read the owner of '{path}'.");
        }

        try
        {
            return new SecurityIdentifier(ownerSid).Value;
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                LocalFree(securityDescriptor);
            }
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
