namespace RevitAgent.Contracts.Events
{
    using System;

    public sealed class DialogShowingEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int ProcessId { get; set; }
        public string DialogId { get; set; } = string.Empty;
        public string DialogTitle { get; set; } = string.Empty;
        public bool IsModal { get; set; }
        public string? HandledResult { get; set; }
    }
}
