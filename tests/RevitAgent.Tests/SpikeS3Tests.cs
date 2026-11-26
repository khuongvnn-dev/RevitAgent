namespace RevitAgent.Tests;

using System;
using RevitAgent.Contracts.Models;
using Xunit;

[Trait("Category", "SpikeS3")]
public class SpikeS3Tests
{
    [Fact]
    public void MepTopology_FuzzyMatcher_ValidatesContinuityWithTolerance()
    {
        // Tolerances: Delta d <= 0.5 mm, Delta theta <= 0.1 deg
        var matcher = new MepTopologyFuzzyMatcher(distanceTolerance: 0.5, angleToleranceDegrees: 0.1);

        // Connector 1: at origin, pointing in +X direction
        var c1 = new MepConnectorPoint("elem-1", 1, x: 100.0, y: 200.0, z: 50.0, dx: 1.0, dy: 0.0, dz: 0.0);

        // Connector 2: within distance tolerance (Delta d = 0.3 mm <= 0.5 mm)
        // facing opposite direction (pointing in -X direction, angle diff = 0.0 deg <= 0.1 deg)
        var c2 = new MepConnectorPoint("elem-2", 1, x: 100.3, y: 200.0, z: 50.0, dx: -1.0, dy: 0.0, dz: 0.0);

        // Connector 3: out of distance tolerance (Delta d = 2.0 mm > 0.5 mm)
        var c3 = new MepConnectorPoint("elem-3", 1, x: 102.0, y: 200.0, z: 50.0, dx: -1.0, dy: 0.0, dz: 0.0);

        // Connector 4: within distance tolerance (0.2 mm) but out of angle tolerance:
        // angle rotated around Z by 0.5 deg > 0.1 deg
        double rad = 0.5 * Math.PI / 180.0;
        double dxRot = -Math.Cos(rad);
        double dyRot = Math.Sin(rad);
        var c4 = new MepConnectorPoint("elem-4", 1, x: 100.2, y: 200.0, z: 50.0, dx: dxRot, dy: dyRot, dz: 0.0);

        // Connector 5: within distance tolerance (0.1 mm) and within angle tolerance (0.05 deg <= 0.1 deg)
        double radSmall = 0.05 * Math.PI / 180.0;
        var c5 = new MepConnectorPoint("elem-5", 1, x: 100.1, y: 200.0, z: 50.0, dx: -Math.Cos(radSmall), dy: Math.Sin(radSmall), dz: 0.0);

        // Check individual continuity
        Assert.True(matcher.AreContinuous(c1, c2), "c1 and c2 must be continuous (Delta d = 0.3mm <= 0.5mm, Delta theta = 0 deg <= 0.1 deg).");
        Assert.False(matcher.AreContinuous(c1, c3), "c1 and c3 must NOT be continuous (Delta d = 2.0mm > 0.5mm).");
        Assert.False(matcher.AreContinuous(c1, c4), "c1 and c4 must NOT be continuous (Delta theta = 0.5 deg > 0.1 deg).");
        Assert.True(matcher.AreContinuous(c1, c5), "c1 and c5 must be continuous (Delta d = 0.1mm, Delta theta = 0.05 deg).");

        // Spatial Hash Grid Test: Add connectors and query
        matcher.AddConnector(c1);
        matcher.AddConnector(c2);
        matcher.AddConnector(c3);
        matcher.AddConnector(c4);

        var matchForC1 = matcher.FindMatchingConnector(c1);
        Assert.NotNull(matchForC1);
        Assert.Equal("elem-2", matchForC1.ElementId);

        // c3 should find no match
        var matchForC3 = matcher.FindMatchingConnector(c3);
        Assert.Null(matchForC3);

        // Test across grid cells with multiple connected pairs
        var matcher2 = new MepTopologyFuzzyMatcher(0.5, 0.1);
        var connA1 = new MepConnectorPoint("pipe-A", 1, 10.0, 10.0, 10.0, 0, 1, 0);
        var connB1 = new MepConnectorPoint("pipe-B", 1, 10.1, 10.2, 10.0, 0, -1, 0); // delta d = sqrt(0.01 + 0.04) = 0.223 mm <= 0.5
        var connC1 = new MepConnectorPoint("pipe-C", 1, 500.0, 500.0, 500.0, 1, 0, 0);
        var connD1 = new MepConnectorPoint("pipe-D", 1, 500.2, 500.0, 500.0, -1, 0, 0); // delta d = 0.2 mm <= 0.5

        matcher2.AddConnectors(new[] { connA1, connB1, connC1, connD1 });
        var pairs = matcher2.FindConnectedPairs();
        Assert.Equal(2, pairs.Count);
    }

}
