using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui;

/// <summary>Small text helpers shared by the UI engine (normalization, clipping, hex, regex).</summary>
public static class UiText
{
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled);

    public static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Text as users type it: whitespace runs (incl. NBSP) → one space, trimmed, Unicode NFC.</summary>
    public static string Norm(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : Whitespace.Replace(s, " ").Trim().Normalize(NormalizationForm.FormC);

    /// <summary>One-line clipped text for snapshot lines and messages.</summary>
    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        s = Whitespace.Replace(s.Replace("\r\n", " \\n ").Replace('\n', ' '), " ").Trim();
        return s.Length > max ? s[..max] + "..." : s;
    }

    public static Regex CompileRegex(string pattern, string what)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw GatewayException.Validation($"Invalid regular expression for {what}: {ex.Message}");
        }
    }

    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

/// <summary>UIA control type ids ↔ names (names as printed in snapshots: Button, Edit, ListItem...).</summary>
public static class ControlTypes
{
    private static readonly string[] Names =
    {
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu",
        "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem",
        "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem",
        "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom",
        "AppBar",
    };

    public const int Button = 50000;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int ListItem = 50007;
    public const int List = 50008;
    public const int MenuItem = 50011;
    public const int RadioButton = 50013;
    public const int Tab = 50018;
    public const int TabItem = 50019;
    public const int Text = 50020;
    public const int Tree = 50023;
    public const int TreeItem = 50024;
    public const int Custom = 50025;
    public const int DataGrid = 50028;
    public const int DataItem = 50029;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;
    public const int Header = 50034;
    public const int HeaderItem = 50035;
    public const int Table = 50036;

    public static string Name(int id)
    {
        var i = id - 50000;
        return i >= 0 && i < Names.Length ? Names[i] : "Unknown";
    }

    public static int? Id(string name)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return 50000 + i;
            }
        }

        return null;
    }

    public static int RequireId(string name) =>
        Id(name) ?? throw GatewayException.Validation(
            $"Unknown control type '{name}'. Use one of: {string.Join(", ", Names)}.");
}
