namespace SymphonyRecomp.Mcp.Scenarios;

public sealed record RouteAggregateDefinition(string Id, int RequiredObservations,
    IReadOnlyList<int> OrderedRooms);
public sealed record RouteTransitionObservation(int From, int To, bool Passed);
public sealed record RouteAggregateArtifact(string Schema, string DefinitionId, string Outcome,
    int AcceptedObservations, int? FailedObservation, int? ExpectedFrom, int? ExpectedTo);

// Parent-owned, game-free campaign input. The definition mirrors the checked-in co-op route
// manifest and the executor accepts one bounded observation list, never paths or loop commands.
// Route v2 uses live-observed telemetry room bytes: 140 is the NO0 lower clock-room junction and
// 220 is the plain-door save room; the route alternates west/east across their shared doorway.
public static class RouteAggregateCatalog
{
    public const string ManifestVersion = "coop-route/1|2";
    public const string SequenceFingerprint = "b1f6e989ccc0cc1484851761c2d8143d1891fafc494a04e1d6be024046f82896";
    public static RouteAggregateDefinition No0MarbleGallery25 { get; } = new(
        "no0-marble-gallery-candidate-25", 25, [140, 220, 140]);
}

public sealed class RouteAggregateExecutor
{
    public const string ArtifactSchema = "sotn-route-aggregate/1";

    public RouteAggregateArtifact Execute(RouteAggregateDefinition definition,
        IReadOnlyList<RouteTransitionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(observations);
        if (definition.RequiredObservations is < 1 or > 64 || definition.OrderedRooms.Count is < 2 or > 32 ||
            observations.Count > definition.RequiredObservations)
            throw new ArgumentOutOfRangeException(nameof(observations), "Route aggregate input exceeds its bound.");
        for (int index = 0; index < observations.Count; index++)
        {
            int segment = index % (definition.OrderedRooms.Count - 1);
            int from = definition.OrderedRooms[segment], to = definition.OrderedRooms[segment + 1];
            RouteTransitionObservation observed = observations[index];
            if (!observed.Passed || observed.From != from || observed.To != to)
                return new(ArtifactSchema, definition.Id, "failed", index, index, from, to);
        }
        string outcome = observations.Count == definition.RequiredObservations ? "passed" : "incomplete";
        int next = observations.Count % (definition.OrderedRooms.Count - 1);
        return new(ArtifactSchema, definition.Id, outcome, observations.Count, null,
            outcome == "passed" ? null : definition.OrderedRooms[next],
            outcome == "passed" ? null : definition.OrderedRooms[next + 1]);
    }
}
