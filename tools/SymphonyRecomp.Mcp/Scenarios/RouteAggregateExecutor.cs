namespace SymphonyRecomp.Mcp.Scenarios;

public sealed record RouteAggregateDefinition(string Id, int RequiredObservations,
    IReadOnlyList<int> OrderedRooms);
public sealed record RouteTransitionObservation(int From, int To, bool Passed);
public sealed record RouteAggregateArtifact(string Schema, string DefinitionId, string Outcome,
    int AcceptedObservations, int? FailedObservation, int? ExpectedFrom, int? ExpectedTo);

// Parent-owned, game-free campaign input. The definition mirrors the checked-in co-op route
// manifest and the executor accepts one bounded observation list, never paths or loop commands.
public static class RouteAggregateCatalog
{
    public const string ManifestVersion = "coop-route/1|1";
    public const string SequenceFingerprint = "34d38244074a0ea351c1374479cafa003bd1a21332e53554fbfba84e30591bac";
    public static RouteAggregateDefinition No0MarbleGallery25 { get; } = new(
        "no0-marble-gallery-candidate-25", 25, [9, 10, 5, 6, 5, 10, 9, 19, 11, 19, 9]);
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
