namespace RevitAgent.Contracts.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents an MEP connector point in 3D coordinate space with an orientation direction vector.
    /// </summary>
    public sealed class MepConnectorPoint
    {
        public string ElementId { get; set; } = string.Empty;
        public int ConnectorId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double DirectionX { get; set; }
        public double DirectionY { get; set; }
        public double DirectionZ { get; set; }

        public MepConnectorPoint()
        {
        }

        public MepConnectorPoint(string elementId, int connectorId, double x, double y, double z, double dx = 0, double dy = 0, double dz = 0)
        {
            ElementId = elementId;
            ConnectorId = connectorId;
            X = x;
            Y = y;
            Z = z;
            DirectionX = dx;
            DirectionY = dy;
            DirectionZ = dz;
        }
    }

    /// <summary>
    /// Spatial Hash Fuzzy Matcher for validating MEP connector continuity with spatial distance (Delta d &lt;= 0.5mm)
    /// and angular orientation (Delta theta &lt;= 0.1 deg) tolerances.
    /// </summary>
    public class MepSpatialFuzzyMatcher
    {
        public const double DefaultDistanceTolerance = 0.5; // 0.5 mm
        public const double DefaultAngleToleranceDeg = 0.1;  // 0.1 degrees

        public double DistanceTolerance { get; }
        public double AngleToleranceDegrees { get; }
        public double CellSize { get; }

        private readonly Dictionary<(int, int, int), List<MepConnectorPoint>> _grid = new Dictionary<(int, int, int), List<MepConnectorPoint>>();
        private readonly List<MepConnectorPoint> _connectors = new List<MepConnectorPoint>();

        public IReadOnlyList<MepConnectorPoint> Connectors => _connectors;

        public MepSpatialFuzzyMatcher(
            double distanceTolerance = DefaultDistanceTolerance,
            double angleToleranceDegrees = DefaultAngleToleranceDeg)
        {
            DistanceTolerance = distanceTolerance;
            AngleToleranceDegrees = angleToleranceDegrees;
            CellSize = distanceTolerance > 0 ? distanceTolerance : DefaultDistanceTolerance;
        }

        public void AddConnector(MepConnectorPoint connector)
        {
            if (connector == null) throw new ArgumentNullException(nameof(connector));

            _connectors.Add(connector);
            var key = GetCellKey(connector.X, connector.Y, connector.Z);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = new List<MepConnectorPoint>();
                _grid[key] = list;
            }
            list.Add(connector);
        }

        public void AddConnectors(IEnumerable<MepConnectorPoint> connectors)
        {
            if (connectors == null) throw new ArgumentNullException(nameof(connectors));
            foreach (var conn in connectors)
            {
                AddConnector(conn);
            }
        }

        public (int, int, int) GetCellKey(double x, double y, double z)
        {
            int cx = (int)Math.Floor(x / CellSize);
            int cy = (int)Math.Floor(y / CellSize);
            int cz = (int)Math.Floor(z / CellSize);
            return (cx, cy, cz);
        }

        public bool AreContinuous(MepConnectorPoint a, MepConnectorPoint b)
        {
            if (a == null || b == null) return false;

            // 1. Spatial distance tolerance check: Delta d <= DistanceTolerance
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist > DistanceTolerance) return false;

            // 2. Angular alignment tolerance check: Delta theta <= AngleToleranceDegrees
            double lenA = Math.Sqrt(a.DirectionX * a.DirectionX + a.DirectionY * a.DirectionY + a.DirectionZ * a.DirectionZ);
            double lenB = Math.Sqrt(b.DirectionX * b.DirectionX + b.DirectionY * b.DirectionY + b.DirectionZ * b.DirectionZ);

            if (lenA > 1e-9 && lenB > 1e-9)
            {
                // Connectors facing each other are opposite: vector A ~= -vector B
                double dotOpposite = (a.DirectionX * -b.DirectionX + a.DirectionY * -b.DirectionY + a.DirectionZ * -b.DirectionZ) / (lenA * lenB);
                dotOpposite = Math.Max(-1.0, Math.Min(1.0, dotOpposite));
                double angleOppositeDeg = Math.Acos(dotOpposite) * (180.0 / Math.PI);

                // Or flowing in same direction: vector A ~= vector B
                double dotParallel = (a.DirectionX * b.DirectionX + a.DirectionY * b.DirectionY + a.DirectionZ * b.DirectionZ) / (lenA * lenB);
                dotParallel = Math.Max(-1.0, Math.Min(1.0, dotParallel));
                double angleParallelDeg = Math.Acos(dotParallel) * (180.0 / Math.PI);

                double minAngleDeg = Math.Min(angleOppositeDeg, angleParallelDeg);
                if (minAngleDeg > AngleToleranceDegrees) return false;
            }

            return true;
        }

        public MepConnectorPoint? FindMatchingConnector(MepConnectorPoint target)
        {
            if (target == null) return null;

            var key = GetCellKey(target.X, target.Y, target.Z);

            // Scan 27 neighboring cells in the spatial hash grid
            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        var neighborKey = (key.Item1 + ox, key.Item2 + oy, key.Item3 + oz);
                        if (_grid.TryGetValue(neighborKey, out var candidates))
                        {
                            foreach (var candidate in candidates)
                            {
                                if (ReferenceEquals(candidate, target)) continue;
                                if (candidate.ElementId == target.ElementId && candidate.ConnectorId == target.ConnectorId) continue;

                                if (AreContinuous(target, candidate))
                                {
                                    return candidate;
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public List<(MepConnectorPoint First, MepConnectorPoint Second)> FindConnectedPairs()
        {
            var pairs = new List<(MepConnectorPoint, MepConnectorPoint)>();
            var visited = new HashSet<string>();

            foreach (var conn in _connectors)
            {
                var match = FindMatchingConnector(conn);
                if (match != null)
                {
                    string idA = $"{conn.ElementId}:{conn.ConnectorId}";
                    string idB = $"{match.ElementId}:{match.ConnectorId}";
                    string pairKey = string.CompareOrdinal(idA, idB) < 0 ? $"{idA}-{idB}" : $"{idB}-{idA}";

                    if (visited.Add(pairKey))
                    {
                        pairs.Add((conn, match));
                    }
                }
            }

            return pairs;
        }
    }

    /// <summary>
    /// Alias for MepSpatialFuzzyMatcher specializing in Connector Topology Graph continuity.
    /// </summary>
    public sealed class MepTopologyFuzzyMatcher : MepSpatialFuzzyMatcher
    {
        public MepTopologyFuzzyMatcher(
            double distanceTolerance = DefaultDistanceTolerance,
            double angleToleranceDegrees = DefaultAngleToleranceDeg)
            : base(distanceTolerance, angleToleranceDegrees)
        {
        }
    }
}
