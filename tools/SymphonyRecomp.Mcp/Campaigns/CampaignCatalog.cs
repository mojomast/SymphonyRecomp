using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Mcp.Campaigns;

public enum CampaignKind { Route, Soak }

public sealed record CampaignDefinition(
    string Id, string Version, CampaignKind Kind, string ModId, string EvidenceVersion,
    int PollMilliseconds, int DeadlineSeconds, int RequiredTransitions,
    string? Stage, int? Area, IReadOnlyList<int> OrderedRooms,
    int RequiredSeconds, int SampleSeconds, int SampleCount, int NoProgressSeconds);

public sealed class CampaignCatalog
{
    public const string Schema = "sotn-campaign/1";
    private static readonly (string Id, string Resource)[] Descriptors =
    [
        ("coop-route-25", "SymphonyRecomp.Mcp.Campaigns.Catalog.coop-route-25.json"),
        ("coop-soak-60m", "SymphonyRecomp.Mcp.Campaigns.Catalog.coop-soak-60m.json"),
    ];

    private readonly IReadOnlyDictionary<string, CampaignDefinition> _definitions;
    public IReadOnlyList<string> Inventory { get; }

    public CampaignCatalog() : this(typeof(CampaignCatalog).Assembly) { }

    internal CampaignCatalog(Assembly assembly)
    {
        var values = new Dictionary<string, CampaignDefinition>(StringComparer.Ordinal);
        foreach ((string id, string resource) in Descriptors)
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The embedded campaign catalog is incomplete.");
            if (stream.Length is <= 0 or > 16 * 1024)
                throw new InvalidOperationException("An embedded campaign is outside its size bound.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096);
            CampaignDefinition definition = Parse(reader.ReadToEnd());
            if (definition.Id != id || !values.TryAdd(id, definition))
                throw new InvalidOperationException("An embedded campaign identity is invalid.");
        }
        if (values.Count != 2) throw new InvalidOperationException("The campaign catalog must contain exactly two entries.");
        _definitions = values;
        Inventory = Array.AsReadOnly(Descriptors.Select(value => value.Id).ToArray());
    }

    public CampaignDefinition Get(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !IsAlphaNumeric(id[0]) ||
            id.Any(character => !IsAlphaNumeric(character) && character is not ('.' or '_' or '-')))
            throw new McpException("id is not a valid campaign catalog identifier.");
        if (!_definitions.TryGetValue(id, out CampaignDefinition? definition))
            throw new McpException("The requested campaign is not in the embedded catalog.");
        return definition;
    }

    internal static CampaignDefinition Parse(string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(source, new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            string schema = Text(root, "schema"), id = Text(root, "id"), version = Text(root, "version");
            string kindText = Text(root, "kind"), modId = Text(root, "modId");
            CampaignKind kind = kindText switch { "route" => CampaignKind.Route, "soak" => CampaignKind.Soak, _ => throw Invalid() };
            string[] expected = kind == CampaignKind.Route
                ? ["schema", "id", "version", "kind", "modId", "routeVersion", "routeFingerprint", "requiredTransitions", "deadlineSeconds", "pollMilliseconds", "stage", "area", "orderedRooms"]
                : ["schema", "id", "version", "kind", "modId", "soakVersion", "requiredSeconds", "sampleSeconds", "sampleCount", "graceSeconds", "pollMilliseconds", "noProgressSeconds"];
            string[] actual = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (actual.Length != expected.Length || actual.Except(expected, StringComparer.Ordinal).Any()) throw Invalid();
            if (schema != Schema || version != "1" || modId != "coop-feasibility") throw Invalid();
            int poll = Integer(root, "pollMilliseconds");
            if (poll is < 50 or > 1000) throw Invalid();
            if (kind == CampaignKind.Route)
            {
                int required = Integer(root, "requiredTransitions"), deadline = Integer(root, "deadlineSeconds");
                JsonElement roomsElement = root.GetProperty("orderedRooms");
                if (roomsElement.ValueKind != JsonValueKind.Array) throw Invalid();
                int[] rooms = roomsElement.EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (id != "coop-route-25" || required != 25 || deadline is <= 0 or > 1800 ||
                    Text(root, "routeVersion") != "no0-marble-gallery-candidate/2" ||
                    Text(root, "stage") != "MarbleGallery" || Integer(root, "area") != 40 ||
                    Text(root, "routeFingerprint") != RouteAggregateCatalog.SequenceFingerprint ||
                    !rooms.SequenceEqual(RouteAggregateCatalog.No0MarbleGallery25.OrderedRooms)) throw Invalid();
                return new(id, version, kind, modId, Text(root, "routeVersion"), poll, deadline,
                    required, "MarbleGallery", 40, Array.AsReadOnly(rooms), 0, 0, 0, 0);
            }
            int seconds = Integer(root, "requiredSeconds"), interval = Integer(root, "sampleSeconds");
            int count = Integer(root, "sampleCount"), grace = Integer(root, "graceSeconds");
            int noProgress = Integer(root, "noProgressSeconds");
            if (id != "coop-soak-60m" || seconds != 3600 || interval != 300 || count != 13 ||
                grace is < 1 or > 60 || noProgress is < 2 or > 30) throw Invalid();
            return new(id, version, kind, modId, Text(root, "soakVersion"), poll, checked(seconds + grace),
                0, null, null, Array.Empty<int>(), seconds, interval, count, noProgress);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new FormatException("The embedded campaign definition is invalid.", exception);
        }
    }

    private static string Text(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrEmpty(text) || text.Length > 128 || text.Any(character => character is < ' ' or > '~')) throw Invalid();
        return text;
    }
    private static int Integer(JsonElement root, string name) => root.GetProperty(name).GetInt32();
    private static InvalidOperationException Invalid() => new("Invalid campaign contract.");
    private static bool IsAlphaNumeric(char value) => value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
