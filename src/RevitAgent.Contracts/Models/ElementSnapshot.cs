namespace RevitAgent.Contracts.Models
{
    using System.Collections.Generic;

    public sealed class BoundingBoxDto
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }

        public BoundingBoxDto()
        {
        }

        public BoundingBoxDto(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }
    }

    public sealed class ElementSnapshot
    {
        public string UniqueId { get; set; } = string.Empty;
        public long ElementId { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<ParameterDto> Parameters { get; set; } = new List<ParameterDto>();
        public BoundingBoxDto? BoundingBox { get; set; }
    }
}
