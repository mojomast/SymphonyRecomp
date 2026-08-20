using System.Reflection;
using System.Text;
using ModelContextProtocol;

namespace SymphonyRecomp.Mcp.Scenarios;

public sealed record ScenarioCatalogEntry(string Id, string Version, string ResourceName);

public sealed class ScenarioCatalog
{
    public const string CoopLocomotionJumpId = "coop-locomotion-jump";
    public const string CoopLocomotionJumpVersion = "2";
    public const int MaximumEntries = 32;

    private static readonly ScenarioCatalogEntry[] StableDescriptors =
    [
        Entry(CoopLocomotionJumpId, CoopLocomotionJumpVersion),
        Entry("coop-transition-west", "4"),
        Entry("coop-contact-hit", "3"),
        Entry("coop-projectile-hit", "3"),
        Entry("coop-damage-revive", "3"),
        Entry("coop-drop-observe", "2"),
    ];

    private readonly IReadOnlyDictionary<string, string> _sources;
    public IReadOnlyList<ScenarioCatalogEntry> Inventory { get; }

    public ScenarioCatalog() : this(typeof(ScenarioCatalog).Assembly, StableDescriptors) { }

    internal ScenarioCatalog(Assembly assembly, IEnumerable<ScenarioCatalogEntry>? descriptors = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ScenarioCatalogEntry[] entries = (descriptors ?? StableDescriptors).ToArray();
        if (entries.Length is < 1 or > MaximumEntries)
            throw new InvalidOperationException("The embedded scenario descriptor table is outside its bound.");
        if (entries.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length ||
            entries.Select(value => value.ResourceName).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidOperationException("The embedded scenario descriptor table contains duplicates.");

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ScenarioCatalogEntry entry in entries)
        {
            using Stream stream = assembly.GetManifestResourceStream(entry.ResourceName)
                ?? throw new InvalidOperationException("The embedded scenario catalog is incomplete.");
            if (stream.Length > ScenarioParser.MaximumSourceBytes)
                throw new InvalidOperationException("An embedded scenario exceeds its size bound.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
            string source;
            try { source = reader.ReadToEnd(); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException("An embedded scenario is not valid UTF-8.", exception);
            }
            ScenarioDefinition scenario;
            try { scenario = ScenarioParser.Parse(source); }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("An embedded scenario is invalid.", exception);
            }
            if (scenario.Id != entry.Id || scenario.Version != entry.Version)
                throw new InvalidOperationException("An embedded scenario identity is invalid.");
            sources.Add(scenario.Id, source);
        }
        Inventory = Array.AsReadOnly(entries);
        _sources = sources;
    }

    private static ScenarioCatalogEntry Entry(string id, string version) => new(id, version,
        $"SymphonyRecomp.Mcp.Scenarios.Catalog.{id}.json");

    public string GetSource(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !IsAlphaNumeric(id[0]) ||
            id.Any(character => !IsAlphaNumeric(character) && character is not ('.' or '_' or '-')))
            throw new McpException("id is not a valid scenario catalog identifier.");
        if (!_sources.TryGetValue(id, out string? source))
            throw new McpException("The requested scenario is not in the embedded catalog.");
        return source;
    }

    private static bool IsAlphaNumeric(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
