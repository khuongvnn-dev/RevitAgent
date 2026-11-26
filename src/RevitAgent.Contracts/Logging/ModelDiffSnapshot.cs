namespace RevitAgent.Contracts.Logging
{
    using System;
    using System.Collections.Generic;
    using RevitAgent.Contracts.Models;

    public sealed class ModelDiffSnapshot
    {
        public string RunId { get; set; } = string.Empty;
        public DateTime PreSnapshotTime { get; set; }
        public DateTime PostSnapshotTime { get; set; }
        public List<string> AddedElements { get; set; } = new List<string>();
        public List<string> RemovedElements { get; set; } = new List<string>();
        public Dictionary<string, List<ParameterDto>> ModifiedElements { get; set; } = new Dictionary<string, List<ParameterDto>>();
        public List<string> NewWarnings { get; set; } = new List<string>();
    }
}
