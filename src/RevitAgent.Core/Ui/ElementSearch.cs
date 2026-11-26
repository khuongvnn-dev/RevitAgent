using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace RevitAgent.Core.Ui;

/// <summary>Outcome of a selector search.</summary>
public sealed class SearchResult
{
    public List<IUIAutomationElement> Matches { get; } = new();

    public int Visited { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>Why the search stopped early (time/element budget), or null when it completed.</summary>
    public string? Stopped { get; set; }
}

/// <summary>
/// Selector search over a subtree : cached depth-first walk in document order instead of
/// one unbounded FindAll, so it can stop at a time budget, skip offscreen subtrees, defer slow subtrees and return
/// as soon as enough onscreen matches are found. Results: onscreen matches first, then offscreen, each in document order.
/// </summary>
public static class ElementSearch
{
    private const int MaxVisited = 100_000;
    private const int SlowChildrenMs = 500;

    public static SearchResult Find(
        UiaCore core,
        IUIAutomationElement root,
        Selector selector,
        int need,
        TimeSpan budget,
        CancellationToken cancellationToken,
        bool includeOffscreen = false,
        bool raw = false)
    {
        var result = new SearchResult();
        var walker = core.Walker(raw);
        var cr = core.NewCacheRequest(UiaCore.SearchProperties);
        var sw = Stopwatch.StartNew();
        var todo = new LinkedList<IUIAutomationElement>();
        var deferred = new List<List<IUIAutomationElement>>();
        var on = new List<IUIAutomationElement>();
        var off = new List<IUIAutomationElement>();
        bool fromEnd = selector.FromEnd;

        foreach (var child in Children(walker, root, cr))
        {
            if (fromEnd)
            {
                todo.AddFirst(child);
            }
            else
            {
                todo.AddLast(child);
            }
        }

        while (todo.Count > 0 || deferred.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (todo.Count == 0)
            {
                var group = deferred[0];
                deferred.RemoveAt(0);
                PushChildren(todo, group, fromEnd);
            }

            if (result.Visited >= MaxVisited)
            {
                result.Stopped = $"{MaxVisited}-element budget";
                break;
            }

            if (sw.Elapsed > budget)
            {
                result.Stopped = $"{budget.TotalSeconds:0.#}s time budget";
                break;
            }

            var e = todo.First!.Value;
            todo.RemoveFirst();
            result.Visited++;
            try
            {
                var name = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_NamePropertyId) as string;
                var aid = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_AutomationIdPropertyId) as string;
                var cls = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_ClassNamePropertyId) as string;
                var ct = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_ControlTypePropertyId) is int i ? i : 0;
                var offscreen = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsOffscreenPropertyId) is true;
                if (selector.Matches(aid, name, cls, ct))
                {
                    (offscreen ? off : on).Add(e);
                    if (on.Count >= need)
                    {
                        break;
                    }
                }

                // Never descend into offscreen containers unless asked (scrolled-away history, collapsed ribbon tabs).
                if (offscreen && !includeOffscreen)
                {
                    continue;
                }

                var t0 = sw.ElapsedMilliseconds;
                var kids = Children(walker, e, cr);
                var cost = sw.ElapsedMilliseconds - t0;
                if (cost > SlowChildrenMs && kids.Count > 0)
                {
                    deferred.Add(kids);
                }
                else
                {
                    PushChildren(todo, kids, fromEnd);
                }
            }
            catch (COMException)
            {
                // Element vanished mid-walk: skip it.
            }
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;
        result.Matches.AddRange(on);
        result.Matches.AddRange(off);
        return result;
    }

    internal static List<IUIAutomationElement> Children(IUIAutomationTreeWalker walker, IUIAutomationElement parent, IUIAutomationCacheRequest cr)
    {
        // One cross-process round trip for ALL children: a cache request with TreeScope Element|Children and the walker's
        // view as tree filter (same view semantics as the walker, incl. promoted descendants of non-control nodes).
        // The sibling-by-sibling walk below costs one round trip per child — on Revit with a large model open each
        // round trip took ~200 ms (live finding 2026-11-25: 16 elements in 5 s). The walk stays as the fallback.
        try
        {
            var batch = cr.Clone();
            batch.TreeScope = TreeScope.TreeScope_Element | TreeScope.TreeScope_Children;
            batch.TreeFilter = walker.condition;
            var cached = parent.BuildUpdatedCache(batch).GetCachedChildren();
            var list = new List<IUIAutomationElement>();
            if (cached is not null)
            {
                int n = Math.Min(cached.Length, 20_000);
                for (int i = 0; i < n; i++)
                {
                    list.Add(cached.GetElement(i));
                }
            }

            return list;
        }
        catch (COMException)
        {
            // Provider does not support cached children: fall back to the walker.
        }

        return WalkChildren(walker, parent, cr);
    }

    private static List<IUIAutomationElement> WalkChildren(IUIAutomationTreeWalker walker, IUIAutomationElement parent, IUIAutomationCacheRequest cr)
    {
        var kids = new List<IUIAutomationElement>();
        IUIAutomationElement? c;
        try
        {
            c = walker.GetFirstChildElementBuildCache(parent, cr);
        }
        catch (COMException)
        {
            return kids;
        }

        while (c is not null && kids.Count < 20_000)
        {
            kids.Add(c);
            try
            {
                c = walker.GetNextSiblingElementBuildCache(c, cr);
            }
            catch (COMException)
            {
                break;
            }
        }

        return kids;
    }

    private static void PushChildren(LinkedList<IUIAutomationElement> todo, List<IUIAutomationElement> kids, bool fromEnd)
    {
        if (fromEnd)
        {
            // Last child visited first.
            foreach (var k in kids)
            {
                todo.AddFirst(k);
            }
        }
        else
        {
            for (int j = kids.Count - 1; j >= 0; j--)
            {
                todo.AddFirst(kids[j]);
            }
        }
    }
}
