using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Interop.UIAutomationClient;

namespace RevitAgent.Core.Ui;

/// <summary>Options of <see cref="TreeSnapshot"/>.</summary>
public sealed class SnapshotOptions
{
    public bool InteractiveOnly { get; init; } = true;

    public int MaxDepth { get; init; } = 25;

    public int MaxNodes { get; init; } = 400;

    /// <summary>Append window-relative bounds (box=x,y,w,h in window pixels) to every line.</summary>
    public bool Bounds { get; init; }

    /// <summary>Include offscreen elements and their subtrees.</summary>
    public bool IncludeOffscreen { get; init; }

    public bool Raw { get; init; }

    /// <summary>Window origin for relative bounds.</summary>
    public int OriginX { get; init; }

    public int OriginY { get; init; }

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Result of a snapshot walk: printable lines plus the nodes that received refs (in ref order).</summary>
public sealed class SnapshotResult
{
    public List<string> Lines { get; } = new();

    public List<UiNode> RefNodes { get; } = new();

    public int Visited { get; set; }

    public bool Truncated { get; set; }

    public bool DepthLimited { get; set; }

    public bool TimedOut { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>Cross-process UIA calls made (one per visited element for its children, plus label lookups).</summary>
    public int RoundTrips { get; set; }

    /// <summary>Raw-view label lookups for buttons/items with an empty or type-like name, and the time they took.</summary>
    public int LabelLookups { get; set; }

    public long LabelMs { get; set; }

    /// <summary>Elements left without a label because label lookups had used their share of the budget.</summary>
    public int LabelsSkipped { get; set; }
}

/// <summary>
/// Accessibility-tree snapshot in its text format: <c>- Type "Name" [ref=eN] id=... value="..." checked (invoke,toggle)</c>.
/// Interactive mode keeps only actionable/labelled nodes; uninteresting containers are transparent.
/// </summary>
public static class TreeSnapshot
{
    private static readonly HashSet<string> InterestingTypes = new(StringComparer.Ordinal)
    {
        "Edit", "Document", "Text", "Window", "Hyperlink", "CheckBox", "RadioButton", "ComboBox", "Button", "MenuItem", "TabItem",
        "ListItem", "TreeItem", "DataItem", "SplitButton", "Slider", "Spinner", "HeaderItem", "Image",
    };

    private static readonly HashSet<string> LabelTypes = new(StringComparer.Ordinal)
    {
        "Button", "ListItem", "TabItem", "MenuItem", "TreeItem", "DataItem", "SplitButton", "HeaderItem",
    };

    private static readonly Regex TypeLikeName = new("^[A-Za-z_][\\w`]*(\\.[A-Za-z_][\\w`+]*){2,}$", RegexOptions.Compiled);

    public static SnapshotResult Take(UiaCore core, IUIAutomationElement start, SnapshotOptions options, Func<int, string> refName, CancellationToken cancellationToken)
    {
        var result = new SnapshotResult();
        var sw = Stopwatch.StartNew();
        var walker = core.Walker(options.Raw);
        var cr = core.NewCacheRequest(UiaCore.NodeProperties);
        var root = start.BuildUpdatedCache(cr);
        result.RoundTrips++;
        var rootNode = UiaCore.ReadCached(root);
        var skipOffscreen = !options.IncludeOffscreen && !rootNode.Offscreen;
        var w = new Walker(core, walker, cr, options, result, refName, skipOffscreen, sw, cancellationToken);
        w.Walk(root, rootNode, 0, 0);
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>actionable, focusable+named, or a meaningful type with a name or id.</summary>
    public static bool IsInteresting(UiNode n) =>
        n.CanInvoke || n.CanToggle || n.CanExpand || n.CanSelect || (n.HasValue && !n.ReadOnly) || n.HasRange ||
        (n.Focusable && n.Name.Length > 0) ||
        (InterestingTypes.Contains(n.Type) && (n.Name.Length > 0 || n.AutomationId.Length > 0));

    public static bool NeedsLabel(UiNode n) =>
        LabelTypes.Contains(n.Type) && (n.Name.Length == 0 || TypeLikeName.IsMatch(n.Name));

    /// <summary>
    /// Types whose raw subtree is small (border, image, TextBlock): their label is read in ONE round trip (subtree cache).
    /// Not TabItem/ListItem/TreeItem/DataItem: their peers can expose the whole tab page or nested rows as children.
    /// </summary>
    private static readonly HashSet<string> SmallSubtreeTypes = new(StringComparer.Ordinal) { "Button", "SplitButton", "MenuItem", "HeaderItem" };

    /// <summary>Visible text of a WPF button/item from the raw view (TextBlocks hidden from the control view).</summary>
    public static string RawLabel(UiaCore core, IUIAutomationElement element) => RawLabel(core, element, null, out _);

    /// <summary>
    /// <see cref="RawLabel(UiaCore, IUIAutomationElement)"/> for an element of control type <paramref name="type"/>, counting the
    /// cross-process <paramref name="roundTrips"/> (each costs ~30-200 ms on a busy Revit). Small-subtree types read their raw
    /// subtree in one call; others (or a provider that refuses a subtree cache) are walked level by level, at most 60 elements.
    /// </summary>
    public static string RawLabel(UiaCore core, IUIAutomationElement element, string? type, out int roundTrips)
    {
        var cr = core.NewCacheRequest(new[] { UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_PropertyIds.UIA_IsOffscreenPropertyId });
        roundTrips = 0;
        if (type is not null && SmallSubtreeTypes.Contains(type))
        {
            try
            {
                var subtree = cr.Clone();
                subtree.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Subtree; // tree filter stays the raw view
                roundTrips++;
                var cached = element.BuildUpdatedCache(subtree);
                return CollectLabel(cached, CachedChildren);
            }
            catch (COMException)
            {
                // Fall back to the level-by-level walk.
            }
        }

        var raw = core.Native.RawViewWalker;
        var trips = 0;
        var label = CollectLabel(element, e =>
        {
            trips++;
            return ElementSearch.Children(raw, e, cr);
        });
        roundTrips += trips;
        return label;
    }

    private static List<IUIAutomationElement> CachedChildren(IUIAutomationElement e)
    {
        var list = new List<IUIAutomationElement>();
        try
        {
            var arr = e.GetCachedChildren();
            for (int i = 0; arr is not null && i < arr.Length; i++)
            {
                list.Add(arr.GetElement(i));
            }
        }
        catch (COMException)
        {
        }

        return list;
    }

    private static string CollectLabel(IUIAutomationElement element, Func<IUIAutomationElement, List<IUIAutomationElement>> children)
    {
        var parts = new List<string>();
        var stack = new Stack<(IUIAutomationElement El, int Level)>();
        stack.Push((element, 0));
        int seen = 0;
        while (stack.Count > 0 && seen < 60 && parts.Count < 6)
        {
            var (cur, lv) = stack.Pop();
            seen++;
            if (lv > 0)
            {
                try
                {
                    if (cur.GetCachedPropertyValue(UIA_PropertyIds.UIA_ControlTypePropertyId) is int ct && ct == ControlTypes.Text &&
                        cur.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsOffscreenPropertyId) is not true &&
                        cur.GetCachedPropertyValue(UIA_PropertyIds.UIA_NamePropertyId) is string { Length: > 0 } nm)
                    {
                        parts.Add(nm.Trim());
                    }
                }
                catch (COMException)
                {
                }
            }

            if (lv >= 4)
            {
                continue;
            }

            var kids = children(cur);
            for (int k = kids.Count - 1; k >= 0; k--)
            {
                stack.Push((kids[k], lv + 1));
            }
        }

        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    public static string FormatNode(UiNode n, string? reference, int indent, UiRect? relativeBounds)
    {
        var sb = new StringBuilder();
        sb.Append(' ', indent * 2).Append("- ").Append(n.Type);
        if (n.Name.Length > 0)
        {
            sb.Append(" \"").Append(UiText.Clip(n.Name, 100)).Append('"');
        }

        if (reference is not null)
        {
            sb.Append(" [ref=").Append(reference).Append(']');
        }

        var attrs = new List<string>();
        if (!string.IsNullOrEmpty(n.Label))
        {
            attrs.Add("label=\"" + UiText.Clip(n.Label, 120) + "\"");
        }

        if (n.AutomationId.Length > 0)
        {
            attrs.Add("id=" + UiText.Clip(n.AutomationId, 60));
        }

        if (n.IsPassword)
        {
            attrs.Add("password");
        }
        else if (n.HasValue && !string.IsNullOrEmpty(n.Value) && n.Value != n.Name)
        {
            attrs.Add("value=\"" + UiText.Clip(n.Value, 80) + "\"");
        }

        if (!n.Enabled)
        {
            attrs.Add("disabled");
        }

        if (n.Focused)
        {
            attrs.Add("focused");
        }

        switch (n.Toggle)
        {
            case 0: attrs.Add("unchecked"); break;
            case 1: attrs.Add("checked"); break;
            case 2: attrs.Add("mixed"); break;
        }

        switch (n.Expand)
        {
            case 0: attrs.Add("collapsed"); break;
            case 1 or 2: attrs.Add("expanded"); break;
        }

        if (n.Selected)
        {
            attrs.Add("selected");
        }

        if (n.Offscreen)
        {
            attrs.Add("offscreen");
        }

        if (relativeBounds is { } b)
        {
            attrs.Add("box=" + b);
        }

        var acts = Actions(n);
        if (attrs.Count > 0)
        {
            sb.Append(' ').Append(string.Join(' ', attrs));
        }

        if (acts.Count > 0)
        {
            sb.Append(" (").Append(string.Join(',', acts)).Append(')');
        }

        return sb.ToString();
    }

    public static List<string> Actions(UiNode n)
    {
        var acts = new List<string>();
        if (n.CanInvoke)
        {
            acts.Add("invoke");
        }

        if (n.CanToggle)
        {
            acts.Add("toggle");
        }

        if (n.CanExpand && n.Expand != 3)
        {
            acts.Add("expand");
        }

        if (n.CanSelect)
        {
            acts.Add("select");
        }

        if (n.HasValue && !n.ReadOnly)
        {
            acts.Add("type");
        }

        if (n.HasRange)
        {
            acts.Add("range");
        }

        if (n.CanScroll)
        {
            acts.Add("scroll");
        }

        if (n.HasText)
        {
            acts.Add("text");
        }

        return acts;
    }

    private sealed class Walker
    {
        private readonly UiaCore _core;
        private readonly IUIAutomationTreeWalker _walker;
        private readonly IUIAutomationCacheRequest _cr;
        private readonly SnapshotOptions _o;
        private readonly SnapshotResult _r;
        private readonly Func<int, string> _refName;
        private readonly bool _skipOffscreen;
        private readonly Stopwatch _sw;
        private readonly CancellationToken _ct;

        public Walker(UiaCore core, IUIAutomationTreeWalker walker, IUIAutomationCacheRequest cr, SnapshotOptions o, SnapshotResult r, Func<int, string> refName, bool skipOffscreen, Stopwatch sw, CancellationToken ct)
        {
            _core = core;
            _walker = walker;
            _cr = cr;
            _o = o;
            _r = r;
            _refName = refName;
            _skipOffscreen = skipOffscreen;
            _sw = sw;
            _ct = ct;
        }

        public void Walk(IUIAutomationElement el, UiNode node, int level, int indent)
        {
            _ct.ThrowIfCancellationRequested();
            if (_r.RefNodes.Count >= _o.MaxNodes || _r.Visited >= _o.MaxNodes * 20)
            {
                _r.Truncated = true;
                return;
            }

            if (_sw.Elapsed > _o.Budget)
            {
                _r.Truncated = true;
                _r.TimedOut = true;
                return;
            }

            _r.Visited++;
            if (level > 0 && _skipOffscreen && node.Offscreen)
            {
                return;
            }

            int childIndent = indent;
            if (!_o.InteractiveOnly || level == 0 || IsInteresting(node))
            {
                if (NeedsLabel(node))
                {
                    // Labels are a nicety: they may use at most a third of the budget, the rest is for the tree itself.
                    if (_r.LabelMs < _o.Budget.TotalMilliseconds / 3)
                    {
                        var t0 = _sw.ElapsedMilliseconds;
                        try
                        {
                            var label = RawLabel(_core, el, node.Type, out var trips);
                            _r.RoundTrips += trips;
                            if (label.Length > 0)
                            {
                                node.Label = label;
                            }
                        }
                        catch (COMException)
                        {
                        }

                        _r.LabelLookups++;
                        _r.LabelMs += _sw.ElapsedMilliseconds - t0;
                    }
                    else
                    {
                        _r.LabelsSkipped++;
                    }
                }

                var reference = _refName(_r.RefNodes.Count + 1);
                _r.RefNodes.Add(node);
                var rel = _o.Bounds && node.Bounds is { } b ? b.RelativeTo(_o.OriginX, _o.OriginY) : (UiRect?)null;
                _r.Lines.Add(FormatNode(node, reference, indent, rel));
                childIndent = indent + 1;
            }

            _r.RoundTrips++;
            var kids = ElementSearch.Children(_walker, el, _cr);
            if (kids.Count == 0)
            {
                return;
            }

            if (level >= _o.MaxDepth)
            {
                _r.DepthLimited = true;
                _r.Lines.Add(new string(' ', childIndent * 2) + "- ... (children hidden; snapshot this ref or raise max_depth)");
                return;
            }

            foreach (var k in kids)
            {
                UiNode kn;
                try
                {
                    kn = UiaCore.ReadCached(k);
                }
                catch (COMException)
                {
                    continue;
                }

                Walk(k, kn, level + 1, childIndent);
                if (_r.Truncated)
                {
                    return;
                }
            }
        }
    }
}
