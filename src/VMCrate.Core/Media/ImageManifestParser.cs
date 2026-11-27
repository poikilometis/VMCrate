using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VMCrate.Core;

internal static partial class ImageManifestParser
{
    [GeneratedRegex(@"(?i)^(?<hash>[a-f0-9]{64})[ \t]+\*?(?<file>[a-z0-9][a-z0-9._+-]*\.iso)[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GnuChecksums();

    [GeneratedRegex(@"(?i)^SHA256[ \t]*\((?<file>[a-z0-9][a-z0-9._+-]*\.iso)\)[ \t]*=[ \t]*(?<hash>[a-f0-9]{64})[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BsdChecksums();

    [GeneratedRegex(@"(?i)(?:^|[-._])(?:amd64|x86_64|x64)(?:[-._]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Amd64();

    [GeneratedRegex("""(?i)href\s*=\s*["']([^"'<>]{1,220})["']""", RegexOptions.CultureInvariant)]
    private static partial Regex Hrefs();

    [GeneratedRegex(@"^[0-9]{2}\.[0-9]{2}(?:\.[0-9]+){0,2}/$")]
    private static partial Regex UbuntuRelease();

    internal static IEnumerable<string> VersionDirectories(string index) =>
        Links(index).Where(link => UbuntuRelease().IsMatch(link)).Distinct();

    internal static IEnumerable<string> PerImageManifests(string index) =>
        Links(index).Where(link => link.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) && IsInstaller(link[..^7]) ||
            link.EndsWith(".sha256sum", StringComparison.OrdinalIgnoreCase) && IsInstaller(link[..^10])).Distinct();

    internal static OfficialImage[] Checksums(ImageSource source, Uri address, string contents) =>
        GnuChecksums().Matches(contents).Concat(BsdChecksums().Matches(contents))
            .Where(match => IsInstaller(match.Groups["file"].Value))
            .Select(match => new OfficialImage(match.Groups["file"].Value, new Uri(address, match.Groups["file"].Value),
                match.Groups["hash"].Value.ToLowerInvariant(), null, source))
            .DistinctBy(image => image.FileName).ToArray();

    internal static OfficialImage[] Fedora(ImageSource source, string contents)
    {
        using var document = JsonDocument.Parse(contents);
        return document.RootElement.EnumerateArray().Select(row => FedoraImage(source, row))
            .OfType<OfficialImage>().ToArray();
    }

    private static OfficialImage? FedoraImage(ImageSource source, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Property(row, "arch") != "x86_64" ||
            !Uri.TryCreate(Property(row, "link"), UriKind.Absolute, out var uri) || !CatalogTransport.SafeUri(uri) ||
            !uri.IdnHost.EndsWith(".fedoraproject.org", StringComparison.Ordinal) ||
            Property(row, "sha256") is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit))
            return null;

        var name = Path.GetFileName(uri.AbsolutePath);
        if (!IsInstaller(name))
            return null;
        long? size = long.TryParse(Property(row, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0 ? parsed : null;
        return new(name, uri, sha.ToLowerInvariant(), size, source);
    }

    private static bool IsInstaller(string name) => name is { Length: > 4 and <= 180 } &&
        name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) &&
        char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+') &&
        Amd64().IsMatch(name);

    private static IEnumerable<string> Links(string html) => Hrefs().Matches(html)
        .Select(match => match.Groups[1].Value);

    private static string? Property(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;
}
