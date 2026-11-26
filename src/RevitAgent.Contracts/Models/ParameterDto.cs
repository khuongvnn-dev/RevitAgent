namespace RevitAgent.Contracts.Models
{
    public sealed class ParameterDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
        public string StorageType { get; set; } = string.Empty;
        public bool IsReadOnly { get; set; }
        public string? UnitType { get; set; }

        public ParameterDto()
        {
        }

        public ParameterDto(string name, string? value, string storageType = "String", bool isReadOnly = false, string? unitType = null)
        {
            Name = name;
            Value = value;
            StorageType = storageType;
            IsReadOnly = isReadOnly;
            UnitType = unitType;
        }
    }
}
