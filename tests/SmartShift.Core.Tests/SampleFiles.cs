namespace SmartShift.Core.Tests;

internal sealed class SampleFiles : IDisposable
{
    private SampleFiles(string path) => Path = path;

    public string Path { get; }

    public static SampleFiles Bytes(string name, byte[] content)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartshift-p1-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, content);
        return new SampleFiles(path);
    }

    public static SampleFiles Text(string name, string content)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartshift-p1-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, content);
        return new SampleFiles(path);
    }

    public static byte[] Png1x1() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static byte[] Jpeg1x1()
    {
        // SOI + SOF0 1x1 + EOI, padded so the parser can read the SOF segment.
        return
        [
            0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9
        ];
    }

    public static byte[] Webp1x1()
    {
        var data = new byte[32];
        data[0] = (byte)'R';
        data[1] = (byte)'I';
        data[2] = (byte)'F';
        data[3] = (byte)'F';
        data[4] = 24;
        data[8] = (byte)'W';
        data[9] = (byte)'E';
        data[10] = (byte)'B';
        data[11] = (byte)'P';
        data[12] = (byte)'V';
        data[13] = (byte)'P';
        data[14] = (byte)'8';
        data[15] = (byte)'X';
        data[16] = 10;
        return data;
    }

    public static byte[] Pdf() => "%PDF-1.7\n%\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"u8.ToArray();

    public static byte[] Gif() => "GIF89a"u8.ToArray().Concat(new byte[26]).ToArray();

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
