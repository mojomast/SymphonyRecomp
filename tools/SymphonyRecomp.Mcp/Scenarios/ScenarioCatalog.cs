using System.Reflection;
using System.Text;
using ModelContextProtocol;

namespace SymphonyRecomp.Mcp.Scenarios;

public sealed class ScenarioCatalog
{
    public const string CoopLocomotionJumpId = "coop-locomotion-jump";
    public const string CoopLocomotionJumpVersion = "1";
    private const string ResourceName =
        "SymphonyRecomp.Mcp.Scenarios.Catalog.coop-locomotion-jump.json";

    private readonly IReadOnlyDictionary<string, string> _sources;

    public ScenarioCatalog() : this(typeof(ScenarioCatalog).Assembly) { }

    internal ScenarioCatalog(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
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
        if (scenario.Id != CoopLocomotionJumpId || scenario.Version != CoopLocomotionJumpVersion)
            throw new InvalidOperationException("An embedded scenario identity is invalid.");
        _sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [scenario.Id] = source,
        };
    }

    public string GetSource(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 ||
            !IsAlphaNumeric(id[0]) ||
            id.Any(character => !IsAlphaNumeric(character) && character is not ('.' or '_' or '-')))
            throw new McpException("id is not a valid scenario catalog identifier.");
        if (!_sources.TryGetValue(id, out string? source))
            throw new McpException("The requested scenario is not in the embedded catalog.");
        return source;
    }

    private static bool IsAlphaNumeric(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
