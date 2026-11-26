using System.Globalization;
using System.Runtime.InteropServices;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>One key stroke: modifiers held + a virtual key or a Unicode character.</summary>
public readonly record struct KeyStroke(ushort[] Modifiers, ushort VirtualKey, char? Unicode, bool Extended)
{
    public override string ToString()
    {
        var mods = string.Concat(Modifiers.Select(m => m switch { 0x10 => "Shift+", 0x11 => "Ctrl+", 0x12 => "Alt+", _ => "?+" }));
        return mods + (Unicode is { } c ? c.ToString() : "VK" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Real keyboard/mouse input with a safety interlock: input is sent only while the foreground window (keyboard) or the
/// window under the click point (mouse) belongs to the target process. If focus is stolen mid-sequence the rest is
/// not sent and TARGET_NOT_READY is raised, so input never lands in another application.
/// </summary>
public static class InputSender
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;

    private static readonly Dictionary<string, (ushort Vk, bool Ext)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = (0x08, false), ["BS"] = (0x08, false), ["BKSP"] = (0x08, false),
        ["BREAK"] = (0x03, false), ["CAPSLOCK"] = (0x14, false),
        ["DELETE"] = (0x2E, true), ["DEL"] = (0x2E, true),
        ["DOWN"] = (0x28, true), ["UP"] = (0x26, true), ["LEFT"] = (0x25, true), ["RIGHT"] = (0x27, true),
        ["END"] = (0x23, true), ["HOME"] = (0x24, true), ["PGDN"] = (0x22, true), ["PGUP"] = (0x21, true),
        ["PAGEDOWN"] = (0x22, true), ["PAGEUP"] = (0x21, true),
        ["ENTER"] = (0x0D, false), ["RETURN"] = (0x0D, false), ["ESC"] = (0x1B, false), ["ESCAPE"] = (0x1B, false),
        ["HELP"] = (0x2F, false), ["INSERT"] = (0x2D, true), ["INS"] = (0x2D, true),
        ["NUMLOCK"] = (0x90, true), ["PRTSC"] = (0x2C, false), ["SCROLLLOCK"] = (0x91, false),
        ["TAB"] = (0x09, false), ["SPACE"] = (0x20, false), ["APPS"] = (0x5D, true),
        ["ADD"] = (0x6B, false), ["SUBTRACT"] = (0x6D, false), ["MULTIPLY"] = (0x6A, false), ["DIVIDE"] = (0x6F, true),
    };

    // ---------------------------------------------------------------- activation

    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground without synthesizing input (AttachThreadInput +
    /// SetForegroundWindow). Returns true when a window of <paramref name="pid"/> is now in the foreground.
    /// </summary>
    public static bool Activate(IntPtr hwnd, int pid, CancellationToken ct = default)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
            Pause(200, ct);
        }

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var fg = NativeMethods.GetForegroundWindow();
            uint me = NativeMethods.GetCurrentThreadId();
            uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            bool a1 = fgThread != 0 && fgThread != me && NativeMethods.AttachThreadInput(me, fgThread, true);
            bool a2 = targetThread != 0 && targetThread != me && targetThread != fgThread && NativeMethods.AttachThreadInput(me, targetThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (a2)
                {
                    NativeMethods.AttachThreadInput(me, targetThread, false);
                }

                if (a1)
                {
                    NativeMethods.AttachThreadInput(me, fgThread, false);
                }
            }

            Pause(80 + (attempt * 120), ct);
            if (ForegroundPid() == pid)
            {
                return true;
            }
        }

        return ForegroundPid() == pid;
    }

    public static int ForegroundPid()
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowProcessId(fg);
    }

    /// <summary>Activates and throws TARGET_NOT_READY when the target process cannot get the foreground.</summary>
    public static void EnsureForeground(IntPtr hwnd, int pid, string why, CancellationToken ct = default)
    {
        if (!Activate(hwnd, pid, ct))
        {
            var fg = TopLevelWindows.Describe(NativeMethods.GetForegroundWindow());
            throw GatewayException.NotReady(
                $"Could not bring the target window to the foreground for {why}; refusing to send real input because it would go to another window" +
                (fg is null ? "." : $" (foreground: \"{UiText.Clip(fg.Title, 60)}\" of {fg.ProcessName}).") +
                " Prefer pattern actions (ui_click without mouse/button options, ui_type without keyboard) which need no focus.");
        }
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>Parses SendKeys syntax: <c>^a</c> Ctrl+A, <c>+{TAB}</c>, <c>%{F4}</c>, <c>{ENTER 3}</c>, <c>~</c> = Enter, <c>^(ac)</c> group.</summary>
    public static List<KeyStroke> ParseSendKeys(string keys)
    {
        var result = new List<KeyStroke>();
        var pending = new List<ushort>();
        int i = 0;
        while (i < keys.Length)
        {
            char c = keys[i];
            switch (c)
            {
                case '+':
                    pending.Add(VkShift);
                    i++;
                    continue;
                case '^':
                    pending.Add(VkControl);
                    i++;
                    continue;
                case '%':
                    pending.Add(VkMenu);
                    i++;
                    continue;
                case '~':
                    result.Add(new KeyStroke(pending.ToArray(), 0x0D, null, false));
                    pending.Clear();
                    i++;
                    continue;
                case '(':
                {
                    int close = keys.IndexOf(')', i + 1);
                    if (close < 0)
                    {
                        throw GatewayException.Validation($"Unbalanced '(' in keys '{keys}'.");
                    }

                    var mods = pending.ToArray();
                    foreach (var inner in ParseSendKeys(keys[(i + 1)..close]))
                    {
                        var all = mods.Concat(inner.Modifiers).Distinct().ToArray();
                        result.Add(inner.Unicode is { } uc && all.Length > 0 ? CharStroke(uc, all) : inner with { Modifiers = all });
                    }

                    pending.Clear();
                    i = close + 1;
                    continue;
                }

                case '{':
                {
                    int close = i + 2 <= keys.Length ? keys.IndexOf('}', i + 2) : -1;
                    if (close < 0)
                    {
                        throw GatewayException.Validation($"Unbalanced '{{' in keys '{keys}'. Write special keys as {{ENTER}}, {{TAB}}, {{F4}}.");
                    }

                    var body = keys[(i + 1)..close];
                    int repeat = 1;
                    var sp = body.LastIndexOf(' ');
                    if (sp > 0 && int.TryParse(body[(sp + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 200)
                    {
                        repeat = n;
                        body = body[..sp];
                    }

                    for (int r = 0; r < repeat; r++)
                    {
                        result.Add(StrokeFor(body, pending.ToArray(), keys));
                    }

                    pending.Clear();
                    i = close + 1;
                    continue;
                }

                default:
                    result.Add(CharStroke(c, pending.ToArray()));
                    pending.Clear();
                    i++;
                    continue;
            }
        }

        if (pending.Count > 0)
        {
            throw GatewayException.Validation($"Keys '{keys}' end with a modifier (+ ^ %) that applies to nothing.");
        }

        return result;
    }

    /// <summary>True when the sequence contains Alt+F4 (closes the window that has focus).</summary>
    public static bool ContainsAltF4(IEnumerable<KeyStroke> strokes) =>
        strokes.Any(s => s.VirtualKey == 0x73 && s.Unicode is null && s.Modifiers.Contains(VkMenu));

    /// <summary>Sends strokes to the foreground window, checking before each stroke that it still belongs to <paramref name="pid"/>.</summary>
    public static void SendStrokes(IReadOnlyList<KeyStroke> strokes, int pid, CancellationToken ct, int delayMs = 15)
    {
        for (int i = 0; i < strokes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            GuardForeground(pid, i, strokes.Count);
            var s = strokes[i];
            var inputs = new List<NativeMethods.Input>();
            foreach (var m in s.Modifiers)
            {
                inputs.Add(Key(m, false, false));
            }

            if (s.Unicode is { } ch && s.Modifiers.Length == 0)
            {
                inputs.Add(UnicodeKey(ch, false));
                inputs.Add(UnicodeKey(ch, true));
            }
            else
            {
                inputs.Add(Key(s.VirtualKey, false, s.Extended));
                inputs.Add(Key(s.VirtualKey, true, s.Extended));
            }

            foreach (var m in s.Modifiers.Reverse())
            {
                inputs.Add(Key(m, true, false));
            }

            try
            {
                Send(inputs);
            }
            catch (GatewayException) when (s.Modifiers.Length > 0)
            {
                // A partially accepted stroke can leave Ctrl/Alt/Shift down for the whole desktop: release them.
                ReleaseModifiers(s.Modifiers);
                throw;
            }
            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Types text as Unicode key events (any keyboard layout, incl. Vietnamese); \n = Enter, \t = Tab.</summary>
    public static void TypeText(string text, int pid, CancellationToken ct, int delayMs = 5)
    {
        var strokes = new List<KeyStroke>(text.Length);
        foreach (var c in text)
        {
            if (c == '\r')
            {
                continue;
            }

            strokes.Add(c switch
            {
                '\n' => new KeyStroke(Array.Empty<ushort>(), 0x0D, null, false),
                '\t' => new KeyStroke(Array.Empty<ushort>(), 0x09, null, false),
                _ => new KeyStroke(Array.Empty<ushort>(), 0, c, false),
            });
        }

        SendStrokes(strokes, pid, ct, delayMs);
    }

    // ---------------------------------------------------------------- mouse

    /// <summary>
    /// Clicks at a screen point (physical pixels) after verifying that the window under the point belongs to
    /// <paramref name="pid"/>. <paramref name="button"/>: left | right | middle.
    /// </summary>
    public static void Click(int x, int y, string button, int count, int pid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GuardPoint(x, y, pid);
        NativeMethods.SetCursorPos(x, y);
        Pause(40, ct);
        uint down = NativeMethods.MouseEventFLeftDown, up = NativeMethods.MouseEventFLeftUp;
        if (string.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
        {
            down = NativeMethods.MouseEventFRightDown;
            up = NativeMethods.MouseEventFRightUp;
        }
        else if (string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase))
        {
            down = NativeMethods.MouseEventFMiddleDown;
            up = NativeMethods.MouseEventFMiddleUp;
        }

        for (int k = 0; k < count; k++)
        {
            ct.ThrowIfCancellationRequested();
            GuardCursor(x, y, pid);
            Send(new List<NativeMethods.Input> { Mouse(down, 0), Mouse(up, 0) });
            Thread.Sleep(count > 1 ? 30 : 40);
        }
    }

    public static void MoveTo(int x, int y, int pid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        GuardPoint(x, y, pid);
        NativeMethods.SetCursorPos(x, y);
    }

    /// <summary>Mouse wheel at a point; <paramref name="delta"/> 120 = one notch up, -120 down. Horizontal when <paramref name="horizontal"/>.</summary>
    public static void Wheel(int x, int y, int delta, bool horizontal, int pid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GuardPoint(x, y, pid);
        NativeMethods.SetCursorPos(x, y);
        Pause(30, ct);
        GuardCursor(x, y, pid);
        Send(new List<NativeMethods.Input> { Mouse(horizontal ? NativeMethods.MouseEventFHWheel : NativeMethods.MouseEventFWheel, unchecked((uint)delta)) });
    }

    /// <summary>
    /// Real drag with stepwise moves (feeds OLE DoDragDrop used by WPF/WinForms drag-drop). The whole path must lie over
    /// windows of <paramref name="pid"/>; if the cursor ends up elsewhere the button is released back at the start point.
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, int pid, CancellationToken ct, int steps = 30)
    {
        ct.ThrowIfCancellationRequested();
        GuardPoint(x1 + 6, y1 + 6, pid);
        for (int k = 0; k <= steps; k++)
        {
            GuardPoint(x1 + ((x2 - x1) * k / steps), y1 + ((y2 - y1) * k / steps), pid);
        }

        NativeMethods.SetCursorPos(x1, y1);
        Pause(120, ct);
        GuardCursor(x1, y1, pid);
        Send(new List<NativeMethods.Input> { Mouse(NativeMethods.MouseEventFLeftDown, 0) });
        try
        {
            Thread.Sleep(150);
            NativeMethods.SetCursorPos(x1 + 6, y1 + 6);
            GuardCursor(x1 + 6, y1 + 6, pid);
            Send(new List<NativeMethods.Input> { Mouse(NativeMethods.MouseEventFMove, 0) });
            Thread.Sleep(80);
            for (int k = 1; k <= steps; k++)
            {
                ct.ThrowIfCancellationRequested();
                int px = x1 + ((x2 - x1) * k / steps), py = y1 + ((y2 - y1) * k / steps);
                NativeMethods.SetCursorPos(px, py);
                GuardCursor(px, py, pid);
                Send(new List<NativeMethods.Input> { Mouse(NativeMethods.MouseEventFMove, 0) });
                Thread.Sleep(25);
            }

            Thread.Sleep(300);
        }
        finally
        {
            // Always release the button, but never over another application: go back to the start point first.
            if (!CursorOverPid(pid))
            {
                NativeMethods.SetCursorPos(x1, y1);
                Thread.Sleep(50);
            }

            Send(new List<NativeMethods.Input> { Mouse(NativeMethods.MouseEventFLeftUp, 0) });
            Thread.Sleep(150);
        }
    }

    // ---------------------------------------------------------------- guards

    private static void GuardForeground(int pid, int sent, int total)
    {
        var fgPid = ForegroundPid();
        if (fgPid != pid)
        {
            var fg = TopLevelWindows.Describe(NativeMethods.GetForegroundWindow());
            throw GatewayException.NotReady(
                $"Focus left the target process after {sent} of {total} key strokes" +
                (fg is null ? string.Empty : $" (foreground is now \"{UiText.Clip(fg.Title, 60)}\" of {fg.ProcessName})") +
                "; the remaining keys were NOT sent. Check the field value and retry.");
        }
    }

    /// <summary>The cursor really is at (x, y) (within 2 px) and over a window of <paramref name="pid"/>; else the user or another program moved it.</summary>
    private static void GuardCursor(int x, int y, int pid)
    {
        if (!NativeMethods.GetCursorPos(out var c) || Math.Abs(c.X - x) > 2 || Math.Abs(c.Y - y) > 2)
        {
            throw GatewayException.NotReady($"The mouse cursor is not where it was placed ({x},{y}; actual {c.X},{c.Y}): someone is using the mouse or the point is not reachable. No click was sent.");
        }

        GuardPoint(c.X, c.Y, pid);
    }

    private static bool CursorOverPid(int pid)
    {
        if (!NativeMethods.GetCursorPos(out var c))
        {
            return false;
        }

        var h = NativeMethods.WindowFromPoint(c);
        var root = h == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(h, NativeMethods.GaRoot);
        return root != IntPtr.Zero && NativeMethods.GetWindowProcessId(root) == pid;
    }

    private static void ReleaseModifiers(IEnumerable<ushort> modifiers)
    {
        try
        {
            var ups = modifiers.Select(m => Key(m, true, false)).ToArray();
            NativeMethods.SendInput((uint)ups.Length, ups, Marshal.SizeOf<NativeMethods.Input>());
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static void Pause(int ms, CancellationToken ct)
    {
        if (ct.CanBeCanceled)
        {
            ct.WaitHandle.WaitOne(ms);
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            Thread.Sleep(ms);
        }
    }

    private static void GuardPoint(int x, int y, int pid)
    {
        var h = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
        var root = h == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(h, NativeMethods.GaRoot);
        var owner = root == IntPtr.Zero ? 0 : NativeMethods.GetWindowProcessId(root);
        if (owner != pid)
        {
            var w = TopLevelWindows.Describe(root);
            throw GatewayException.NotReady(
                $"The point {x},{y} is covered by " + (w is null ? "no window" : $"\"{UiText.Clip(w.Title, 60)}\" ({w.ProcessName}, pid {w.ProcessId})") +
                $", not by process {pid}; refusing to click. Bring the target window to the front or scroll the element into view.");
        }
    }

    private static KeyStroke StrokeFor(string name, ushort[] mods, string all)
    {
        if (name.Length == 1)
        {
            return CharStroke(name[0], mods);
        }

        if (NamedKeys.TryGetValue(name, out var k))
        {
            return new KeyStroke(mods, k.Vk, null, k.Ext);
        }

        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            return new KeyStroke(mods, (ushort)(0x70 + f - 1), null, false);
        }

        throw GatewayException.Validation(
            $"Unknown key '{{{name}}}' in '{all}'. Use ENTER, TAB, ESC, BACKSPACE, DELETE, INSERT, HOME, END, PGUP, PGDN, UP, DOWN, LEFT, RIGHT, SPACE, F1..F24, APPS.");
    }

    private static KeyStroke CharStroke(char c, ushort[] mods)
    {
        if (mods.Length == 0)
        {
            return new KeyStroke(mods, 0, c, false);
        }

        // With modifiers the app expects a virtual key (Ctrl+A is VK_A + Ctrl, not the Unicode 'a').
        var scan = NativeMethods.VkKeyScanW(c);
        if (scan == -1)
        {
            throw GatewayException.Validation($"Character '{c}' cannot be combined with modifiers on the current keyboard layout.");
        }

        var vk = (ushort)(scan & 0xFF);
        var shiftState = (scan >> 8) & 0xFF;
        var all = mods.ToList();
        if ((shiftState & 1) != 0 && !all.Contains(VkShift))
        {
            all.Add(VkShift);
        }

        return new KeyStroke(all.ToArray(), vk, null, false);
    }

    private static NativeMethods.Input Key(ushort vk, bool up, bool extended)
    {
        var flags = (up ? NativeMethods.KeyEventFKeyUp : 0) | (extended ? NativeMethods.KeyEventFExtendedKey : 0);
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeybdInput { WVk = vk, WScan = (ushort)NativeMethods.MapVirtualKeyW(vk, 0), DwFlags = flags },
            },
        };
    }

    private static NativeMethods.Input UnicodeKey(char c, bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        U = new NativeMethods.InputUnion
        {
            Ki = new NativeMethods.KeybdInput { WVk = 0, WScan = c, DwFlags = NativeMethods.KeyEventFUnicode | (up ? NativeMethods.KeyEventFKeyUp : 0) },
        },
    };

    private static NativeMethods.Input Mouse(uint flags, uint data) => new()
    {
        Type = NativeMethods.InputMouse,
        U = new NativeMethods.InputUnion { Mi = new NativeMethods.MouseInput { DwFlags = flags, MouseData = data } },
    };

    private static void Send(List<NativeMethods.Input> inputs)
    {
        var arr = inputs.ToArray();
        var sent = NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.Input>());
        if (sent != arr.Length)
        {
            throw GatewayException.NotReady(
                $"Windows rejected the synthesized input (SendInput sent {sent}/{arr.Length}, error {Marshal.GetLastWin32Error()}). " +
                "The target may run elevated (UIPI) or the desktop is locked.");
        }
    }
}
