namespace RevitAgent.Contracts.Events
{
    using System;

    public sealed class SessionStateEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int ProcessId { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? ActiveDocumentPath { get; set; }
    }

    public static class SessionStates
    {
        public const string Starting = "Starting";
        public const string Ready = "Ready";
        public const string RunningCommand = "RunningCommand";
        public const string Error = "Error";
        public const string Exited = "Exited";
    }
}
