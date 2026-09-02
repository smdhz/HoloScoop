namespace HoloScoop.Services.Note;

public sealed class SpeakerNameCatalog
{
    private IReadOnlyList<string> _names = [];
    private IReadOnlyDictionary<string, string> _canonicalNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Names => _names;

    public void Initialize(IEnumerable<string> names)
    {
        var canonicalNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _canonicalNames = canonicalNames.ToDictionary(
            name => name,
            name => name,
            StringComparer.OrdinalIgnoreCase);
        _names = canonicalNames;
    }

    public bool TryGetCanonicalName(string name, out string canonicalName)
        => _canonicalNames.TryGetValue(name.Trim(), out canonicalName!);
}
