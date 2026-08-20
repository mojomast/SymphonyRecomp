using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class RouteAggregateExecutorTests
{
    [Fact]
    public void ManifestDerivedDefinitionConsumesExactOrdered25AndEmitsBoundedArtifact()
    {
        RouteAggregateDefinition definition = RouteAggregateCatalog.No0MarbleGallery25;
        RouteTransitionObservation[] observations = Enumerable.Range(0, 25).Select(index =>
        {
            int segment = index % (definition.OrderedRooms.Count - 1);
            return new RouteTransitionObservation(definition.OrderedRooms[segment],
                definition.OrderedRooms[segment + 1], true);
        }).ToArray();

        RouteAggregateArtifact result = new RouteAggregateExecutor().Execute(definition, observations);

        Assert.Equal("passed", result.Outcome);
        Assert.Equal(25, result.AcceptedObservations);
        Assert.Null(result.FailedObservation);
    }

    [Fact]
    public void FirstMismatchStopsAndIncompleteInputReportsOnlyNextExpectedEdge()
    {
        var executor = new RouteAggregateExecutor();
        RouteAggregateDefinition definition = RouteAggregateCatalog.No0MarbleGallery25;
        RouteAggregateArtifact failed = executor.Execute(definition,
            [new(9, 10, true), new(10, 19, true), new(5, 6, true)]);
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal(1, failed.AcceptedObservations);
        Assert.Equal(1, failed.FailedObservation);
        RouteAggregateArtifact incomplete = executor.Execute(definition, [new(9, 10, true)]);
        Assert.Equal("incomplete", incomplete.Outcome);
        Assert.Equal(10, incomplete.ExpectedFrom);
        Assert.Equal(5, incomplete.ExpectedTo);
    }
}
