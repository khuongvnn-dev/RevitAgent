using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Interop.UIAutomationClient;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Owns the FlaUI <see cref="UIA3Automation"/> (created lazily on an MTA worker thread) and the native cache
/// requests used for fast tree walks: one cross-process call per element instead of one per property.
/// </summary>
public sealed class UiaCore : IDisposable
{
    internal static readonly int[] NodeProperties =
    {
        UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_PropertyIds.UIA_AutomationIdPropertyId,
        UIA_PropertyIds.UIA_ClassNamePropertyId, UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA_PropertyIds.UIA_IsEnabledPropertyId,
        UIA_PropertyIds.UIA_HasKeyboardFocusPropertyId, UIA_PropertyIds.UIA_IsKeyboardFocusablePropertyId,
        UIA_PropertyIds.UIA_NativeWindowHandlePropertyId, UIA_PropertyIds.UIA_BoundingRectanglePropertyId,
        UIA_PropertyIds.UIA_IsPasswordPropertyId, UIA_PropertyIds.UIA_RuntimeIdPropertyId,
        UIA_PropertyIds.UIA_IsInvokePatternAvailablePropertyId, UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsTogglePatternAvailablePropertyId, UIA_PropertyIds.UIA_IsExpandCollapsePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsSelectionItemPatternAvailablePropertyId, UIA_PropertyIds.UIA_IsScrollPatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId, UIA_PropertyIds.UIA_IsRangeValuePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_ValueValuePropertyId, UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
        UIA_PropertyIds.UIA_ToggleToggleStatePropertyId, UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId,
        UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId,
    };

    internal static readonly int[] SearchProperties =
    {
        UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_AutomationIdPropertyId, UIA_PropertyIds.UIA_ClassNamePropertyId,
        UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA_PropertyIds.UIA_RuntimeIdPropertyId,
    };

    private readonly object _gate = new();
    private UIA3Automation? _automation;
    private bool _disposed;

    /// <summary>The FlaUI automation (created on first use).</summary>
    public UIA3Automation Automation
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_automation is null)
                {
                    var a = new UIA3Automation();
                    try
                    {
                        // A hung provider (Revit busy in a command) then fails fast instead of blocking ~20 s per call.
                        a.ConnectionTimeout = TimeSpan.FromSeconds(3);
                        a.TransactionTimeout = TimeSpan.FromSeconds(10);
                    }
                    catch (Exception)
                    {
                        // Older UIA (IUIAutomation2 missing): our own wall-clock budget still applies.
                    }

                    _automation = a;
                }

                return _automation;
            }
        }
    }

    public IUIAutomation Native => Automation.NativeAutomation;

    public IUIAutomationCacheRequest NewCacheRequest(IEnumerable<int> properties)
    {
        var cr = Native.CreateCacheRequest();
        foreach (var p in properties)
        {
            cr.AddProperty(p);
        }

        cr.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Element;
        cr.TreeFilter = Native.RawViewCondition;
        cr.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        return cr;
    }

    public IUIAutomationTreeWalker Walker(bool raw) => raw ? Native.RawViewWalker : Native.ControlViewWalker;

    public IUIAutomationElement FromHandle(long hwnd) => Native.ElementFromHandle(new IntPtr(hwnd));

    public AutomationElement Wrap(IUIAutomationElement native) => Automation.WrapNativeElement(native);

    public static IUIAutomationElement Unwrap(AutomationElement element) =>
        ((UIA3FrameworkAutomationElement)element.FrameworkAutomationElement).NativeElement;

    /// <summary>Reads the cached properties of an element obtained with a <see cref="NodeProperties"/> cache request.</summary>
    public static UiNode ReadCached(IUIAutomationElement e) => new(e, cached: true);

    /// <summary>Fetches all node properties of an element in one round trip.</summary>
    public UiNode ReadLive(IUIAutomationElement e) => new(e.BuildUpdatedCache(NewCacheRequest(NodeProperties)), cached: true);

    public bool SameElement(IUIAutomationElement a, IUIAutomationElement b)
    {
        try
        {
            return Native.CompareElements(a, b) != 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try
            {
                _automation?.Dispose();
            }
            catch (Exception)
            {
                // Best effort on shutdown.
            }

            _automation = null;
        }
    }

    // ---------------------------------------------------------------- errors

    public const int UiaElementNotAvailable = unchecked((int)0x80040201);
    public const int UiaElementNotEnabled = unchecked((int)0x80040200);
    public const int UiaNoClickablePoint = unchecked((int)0x80040202);
    public const int UiaTimeout = unchecked((int)0x80131505);
    public const int RpcServerUnavailable = unchecked((int)0x800706BA);
    public const int RpcCallRejected = unchecked((int)0x80010001);
    public const int RpcServerCallRetryLater = unchecked((int)0x8001010A);
    public const int EAccessDenied = unchecked((int)0x80070005);

    /// <summary>Maps UIA/COM/FlaUI failures to agent-actionable gateway errors (null when not a known UIA failure).</summary>
    public static GatewayException? Translate(Exception ex)
    {
        var hr = ex is COMException ? ex.HResult : ex.InnerException is COMException ic ? ic.HResult : 0;
        var typeName = ex.GetType().Name;
        if (hr == UiaElementNotAvailable || typeName == "ElementNotAvailableException")
        {
            return GatewayException.NotFound("The element is no longer available (the UI changed or the window closed). Take a new ui_snapshot.");
        }

        if (hr == UiaElementNotEnabled || typeName == "ElementNotEnabledException")
        {
            return GatewayException.NotReady("The element is disabled.");
        }

        if (hr == UiaNoClickablePoint || typeName == "NoClickablePointException")
        {
            return GatewayException.NotReady("The element has no clickable point (offscreen, hidden or virtualized). Scroll it into view (ui_scroll direction=into) first.");
        }

        if (hr == UiaTimeout || hr == RpcCallRejected || hr == RpcServerCallRetryLater || ex is TimeoutException || typeName == "TimeoutException")
        {
            return GatewayException.NotReady("Target UI not responding (UI Automation timed out; the application is busy or blocked by a modal dialog). Check ui_list_windows / revit_get_state and retry.");
        }

        if (hr == RpcServerUnavailable)
        {
            return GatewayException.NotFound("The target process is gone (UI Automation lost the connection). Call ui_list_windows.");
        }

        if (hr == EAccessDenied || ex is UnauthorizedAccessException)
        {
            return new GatewayException(
                GatewayErrorCodes.PermissionDenied,
                "Access denied by the target UI. If the application runs as Administrator, the gateway must run elevated too (Windows UIPI).");
        }

        if (typeName is "PatternNotSupportedException" or "PropertyNotSupportedException" or "MethodNotSupportedException")
        {
            return GatewayException.Validation("The element does not support this operation: " + ex.Message);
        }

        if (ex is InvalidComObjectException)
        {
            return GatewayException.NotFound("The element is no longer available (its UI Automation object was released). Take a new ui_snapshot.");
        }

        if (ex is COMException)
        {
            return GatewayException.NotReady($"UI Automation call failed (0x{hr:X8}). The UI may be changing or busy; take a new snapshot and retry.");
        }

        return null;
    }
}

/// <summary>Properties of one element, read from the UIA cache (one round trip per element).</summary>
public sealed class UiNode
{
    internal UiNode(IUIAutomationElement e, bool cached)
    {
        Element = e;
        object Get(int id) => cached ? e.GetCachedPropertyValue(id) : e.GetCurrentPropertyValue(id);
        string S(int id) => Get(id) as string ?? string.Empty;
        bool B(int id) => Get(id) is bool b && b;

        Name = S(UIA_PropertyIds.UIA_NamePropertyId);
        ControlTypeId = Get(UIA_PropertyIds.UIA_ControlTypePropertyId) is int ct ? ct : 0;
        AutomationId = S(UIA_PropertyIds.UIA_AutomationIdPropertyId);
        ClassName = S(UIA_PropertyIds.UIA_ClassNamePropertyId);
        Offscreen = B(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        Enabled = Get(UIA_PropertyIds.UIA_IsEnabledPropertyId) is not bool en || en;
        Focused = B(UIA_PropertyIds.UIA_HasKeyboardFocusPropertyId);
        Focusable = B(UIA_PropertyIds.UIA_IsKeyboardFocusablePropertyId);
        NativeWindowHandle = Get(UIA_PropertyIds.UIA_NativeWindowHandlePropertyId) switch
        {
            int i => i,
            long l => l,
            IntPtr p => p.ToInt64(),
            _ => 0,
        };
        if (Get(UIA_PropertyIds.UIA_BoundingRectanglePropertyId) is double[] { Length: 4 } r && r[2] > 0 && r[3] > 0)
        {
            Bounds = new UiRect((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]));
        }

        IsPassword = B(UIA_PropertyIds.UIA_IsPasswordPropertyId);
        RuntimeId = Get(UIA_PropertyIds.UIA_RuntimeIdPropertyId) as int[];
        CanInvoke = B(UIA_PropertyIds.UIA_IsInvokePatternAvailablePropertyId);
        HasValue = B(UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId);
        CanToggle = B(UIA_PropertyIds.UIA_IsTogglePatternAvailablePropertyId);
        CanExpand = B(UIA_PropertyIds.UIA_IsExpandCollapsePatternAvailablePropertyId);
        CanSelect = B(UIA_PropertyIds.UIA_IsSelectionItemPatternAvailablePropertyId);
        CanScroll = B(UIA_PropertyIds.UIA_IsScrollPatternAvailablePropertyId);
        HasText = B(UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId);
        HasRange = B(UIA_PropertyIds.UIA_IsRangeValuePatternAvailablePropertyId);
        if (HasValue)
        {
            // Never keep a password field's value (it is not shown, returned or cached).
            Value = IsPassword ? null : Get(UIA_PropertyIds.UIA_ValueValuePropertyId) as string;
            ReadOnly = B(UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId);
        }

        if (CanToggle && Get(UIA_PropertyIds.UIA_ToggleToggleStatePropertyId) is int ts)
        {
            Toggle = ts;
        }

        if (CanExpand && Get(UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId) is int es)
        {
            Expand = es;
        }

        if (CanSelect)
        {
            Selected = B(UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId);
        }
    }

    public IUIAutomationElement Element { get; }

    public string Name { get; }

    public int ControlTypeId { get; }

    public string Type => ControlTypes.Name(ControlTypeId);

    public string AutomationId { get; }

    public string ClassName { get; }

    public bool Offscreen { get; }

    public bool Enabled { get; }

    public bool Focused { get; }

    public bool Focusable { get; }

    public long NativeWindowHandle { get; }

    public UiRect? Bounds { get; }

    public bool IsPassword { get; }

    public int[]? RuntimeId { get; }

    public bool CanInvoke { get; }

    public bool HasValue { get; }

    public bool CanToggle { get; }

    public bool CanExpand { get; }

    public bool CanSelect { get; }

    public bool CanScroll { get; }

    public bool HasText { get; }

    public bool HasRange { get; }

    public string? Value { get; }

    public bool ReadOnly { get; }

    /// <summary>0 off, 1 on, 2 indeterminate.</summary>
    public int? Toggle { get; }

    /// <summary>0 collapsed, 1 expanded, 2 partially expanded, 3 leaf.</summary>
    public int? Expand { get; }

    public bool Selected { get; }

    /// <summary>Visible text read from the raw view for WPF buttons/items whose Name is empty or a view-model type name.</summary>
    public string? Label { get; set; }

    public string Describe() => $"{Type} \"{UiText.Clip(Name.Length > 0 ? Name : Label ?? AutomationId, 60)}\"";

    public string? ToggleText => Toggle switch
    {
        0 => "off",
        1 => "on",
        2 => "indeterminate",
        _ => null,
    };

    public string? ExpandText => Expand switch
    {
        0 => "collapsed",
        1 => "expanded",
        2 => "partially_expanded",
        3 => "leaf",
        _ => null,
    };
}

/// <summary>Screen rectangle in physical pixels.</summary>
public readonly record struct UiRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + (Width / 2);

    public int CenterY => Y + (Height / 2);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    public UiRect RelativeTo(int originX, int originY) => new(X - originX, Y - originY, Width, Height);

    public override string ToString() => $"{X},{Y},{Width},{Height}";
}
