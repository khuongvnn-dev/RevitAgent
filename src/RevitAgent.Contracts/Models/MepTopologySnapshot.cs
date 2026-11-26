namespace RevitAgent.Contracts.Models
{
    using System.Collections.Generic;

    public sealed class MepTopologySnapshot
    {
        public List<MepConnectionEdge> Connections { get; set; } = new List<MepConnectionEdge>();
        public List<MepElementNode> Elements { get; set; } = new List<MepElementNode>();
    }

    public sealed class MepConnectionEdge
    {
        public string FromUniqueId { get; set; } = string.Empty;
        public string ToUniqueId { get; set; } = string.Empty;
        public bool IsPhysicallyConnected { get; set; }
    }

    public sealed class MepElementNode
    {
        public string UniqueId { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double CenterZ { get; set; }
    }
}
