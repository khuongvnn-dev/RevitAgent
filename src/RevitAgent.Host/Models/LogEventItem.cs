namespace RevitAgent.Host.Models
{
    using System.Windows.Media;

    public class LogEventItem
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public Brush BadgeColor { get; set; } = Brushes.Gray;
    }
}
