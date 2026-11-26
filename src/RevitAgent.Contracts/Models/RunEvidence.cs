namespace RevitAgent.Contracts.Models
{
    using System;
    using System.Collections.Generic;

    public sealed class RunEvidence
    {
        public string RunId { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;
        public DateTime StartTimeUtc { get; set; }
        public DateTime? EndTimeUtc { get; set; }
        public bool Passed { get; set; }
        public string? FailureReason { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public string? TimelinePath { get; set; }
        public string? IpcTracePath { get; set; }
    }
}
