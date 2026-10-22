namespace SmartShift.Core.IO;

public static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "unknown size";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            value /= 1024d;
            if (value < 1024d)
            {
                return $"{value:0.##} {unit}";
            }
        }

        return $"{value:0.##} PB";
    }
}
