namespace RevitAgent.Host.Models
{
    using System;
    using System.Globalization;
    using RevitAgent.Core.Revit;

    /// <summary>One running Revit shown in the dashboard list (a snapshot of <see cref="RevitInstance"/>).</summary>
    public sealed class RevitInstanceItem
    {
        public const string Yes = "✓";
        public const string No = "✗";

        public int Pid { get; init; }

        public string YearText { get; init; } = "-";

        public string Title { get; init; } = string.Empty;

        /// <summary>ready | busy | modal | not_responding.</summary>
        public string State { get; init; } = "ready";

        public bool BridgeAvailable { get; init; }

        public bool LaunchedByGateway { get; init; }

        /// <summary>Main window handle, or <see cref="IntPtr.Zero"/> when Revit has no main window yet.</summary>
        public IntPtr MainWindow { get; init; }

        /// <summary>True when the Host and this Revit share the same elevation (UI automation needs parity).</summary>
        public bool UacMatched { get; init; } = true;

        public string BridgeText => BridgeAvailable ? Yes : No;

        public string LaunchedText => LaunchedByGateway ? Yes : No;

        /// <summary>Identity of everything displayed; the list is only rebuilt when it changes.</summary>
        public string Signature => string.Join("|", Pid, YearText, Title, State, BridgeAvailable, LaunchedByGateway, MainWindow, UacMatched);

        public static RevitInstanceItem From(RevitInstance instance, bool uacMatched) => new()
        {
            Pid = instance.Pid,
            YearText = instance.Year?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Title = FirstNonEmpty(instance.ActiveDocumentGuess, instance.MainWindowTitle),
            State = string.IsNullOrWhiteSpace(instance.State) ? "ready" : instance.State,
            BridgeAvailable = instance.BridgePipeAvailable == true,
            LaunchedByGateway = instance.LaunchedByGateway,
            MainWindow = ParseHwnd(instance.MainWindowHwnd),
            UacMatched = uacMatched,
        };

        public static IntPtr ParseHwnd(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return IntPtr.Zero;
            }

            var text = hex.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? new IntPtr(value) : IntPtr.Zero;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v!;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>An installed Revit release (from the revit_list_installed tool).</summary>
    public sealed class InstalledRevitItem
    {
        public int Year { get; init; }

        public string ExePath { get; init; } = string.Empty;

        public bool BridgeInstalled { get; init; }

        public string YearText => Year.ToString(CultureInfo.InvariantCulture);

        public string BridgeText => BridgeInstalled ? RevitInstanceItem.Yes : RevitInstanceItem.No;

        public override string ToString() => $"Revit {Year}";
    }
}
