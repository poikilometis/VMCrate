using System.IO;
using System.Text;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class InboxChecks
{
    internal static void WindowsUdf()
    {
        using var temp = new TemporaryDirectory();
        var source = temp.File("sample.txt", "one way");
        var staging = Directory.CreateDirectory(Path.Combine(temp.Root, "stage")).FullName;
        var iso = Path.Combine(temp.Root, "inbox.iso");
        new UdfInboxWriter().Write([source], staging, iso);

        using var stream = File.OpenRead(iso);
        Expect(stream.Length > 32 * 1024, "IMAPI did not write a complete disc image.");
        var header = new byte[Math.Min(stream.Length, 256 * 1024)];
        stream.ReadExactly(header);
        Expect(Encoding.ASCII.GetString(header).Contains("NSR02", StringComparison.Ordinal),
            "The image did not contain a UDF 1.02 volume descriptor.");
        Expect(File.ReadAllText(source) == "one way", "The source was changed.");
    }
}
