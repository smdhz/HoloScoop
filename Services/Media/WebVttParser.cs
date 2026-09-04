using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HoloScoop.Services.Media;

public sealed record ParsedSubtitleCue(int Sequence, long StartMs, long EndMs, string Text);

public interface ISubtitleParser
{
    Task<IReadOnlyList<ParsedSubtitleCue>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}

public sealed partial class WebVttParser : ISubtitleParser
{
    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    public async Task<IReadOnlyList<ParsedSubtitleCue>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var cues = new List<ParsedSubtitleCue>();
        var block = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                ParseBlock(block, cues);
                block.Clear();
            }
            else
            {
                block.Add(line.TrimEnd('\r'));
            }
        }

        ParseBlock(block, cues);
        return cues;
    }

    private static void ParseBlock(List<string> lines, List<ParsedSubtitleCue> cues)
    {
        if (lines.Count == 0 ||
            lines[0].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
            lines[0].StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
            lines[0].Equals("STYLE", StringComparison.OrdinalIgnoreCase) ||
            lines[0].Equals("REGION", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var timingIndex = lines.FindIndex(static line => line.Contains("-->", StringComparison.Ordinal));
        if (timingIndex < 0)
        {
            return;
        }

        var timingParts = lines[timingIndex].Split("-->", 2, StringSplitOptions.TrimEntries);
        var endToken = timingParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!TryParseTimestamp(timingParts[0], out var startMs) ||
            !TryParseTimestamp(endToken, out var endMs) ||
            endMs <= startMs)
        {
            return;
        }

        var text = string.Join(' ', lines.Skip(timingIndex + 1));
        text = WebUtility.HtmlDecode(TagPattern().Replace(text, " "));
        text = WhitespacePattern().Replace(text, " ").Trim();
        if (text.Length == 0)
        {
            return;
        }

        cues.Add(new ParsedSubtitleCue(cues.Count, startMs, endMs, text));
    }

    private static bool TryParseTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0L;
        var minuteIndex = 0;
        if (parts.Length == 3)
        {
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            minuteIndex = 1;
        }

        if (!long.TryParse(parts[minuteIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !decimal.TryParse(parts[minuteIndex + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            minutes is < 0 or > 59 || seconds is < 0 or >= 60 || hours < 0)
        {
            return false;
        }

        try
        {
            milliseconds = checked(hours * 3_600_000 + minutes * 60_000 +
                decimal.ToInt64(decimal.Truncate(seconds * 1000)));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
