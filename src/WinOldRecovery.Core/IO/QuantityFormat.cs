using System.Globalization;

namespace WinOldRecovery.Core.IO;

public static class QuantityFormat
{
    public static string Count(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Bytes(long value)
    {
        const double kibi = 1024d;
        if (Math.Abs(value) >= kibi * kibi * kibi)
        {
            return (value / (kibi * kibi * kibi)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        if (Math.Abs(value) >= kibi * kibi)
        {
            return (value / (kibi * kibi)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        if (Math.Abs(value) >= kibi)
        {
            return (value / kibi).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        return Count(value) + " bytes";
    }
}
