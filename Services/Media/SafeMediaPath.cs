using System.Text.RegularExpressions;

namespace HoloScoop.Services.Media;

internal static partial class SafeMediaPath
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalIdPattern();

    public static string ValidateExternalId(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId) || !ExternalIdPattern().IsMatch(externalId))
        {
            throw new ArgumentException("The external media ID contains unsupported characters.", nameof(externalId));
        }

        return externalId;
    }

    public static string UnderRoot(string root, params string[] components)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine([fullRoot, .. components]));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.Ordinal) &&
            !string.Equals(candidate, fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved media path escapes its configured root.");
        }

        return candidate;
    }

    public static void ValidateYouTubeUrl(Uri sourceUrl)
    {
        if (!sourceUrl.IsAbsoluteUri || sourceUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only absolute HTTPS source URLs are accepted.", nameof(sourceUrl));
        }

        var host = sourceUrl.IdnHost;
        if (!host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only YouTube source URLs are accepted.", nameof(sourceUrl));
        }
    }
}

