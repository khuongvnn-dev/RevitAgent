using System.Diagnostics;
using System.IO;
using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// A helper process the test starts itself (powershell.exe) that shows a REAL dialog: a WinForms MessageBox owned by
/// a form (main window disabled, like Revit), a comctl32 TaskDialog (DirectUIHWND, like Revit's TaskDialog), a form
/// that asks "save changes?" when closed, or a window-less dummy. The answer the dialog returned is written to
/// <see cref="ResultFile"/>, so tests can verify exactly which button the gateway clicked.
/// Tests only touch processes created here, and Dispose always kills them.
/// </summary>
internal sealed class DialogHostProcess : IDisposable
{
    public const string MainTitle = "RevitAgent Test Host";

    private const string MessageBoxScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'RevitAgent Test Host'
        $form.Width = 520; $form.Height = 320
        $form.Add_Shown({
            $r = [System.Windows.Forms.MessageBox]::Show($form, $Text, $Title, $Buttons)
            [IO.File]::WriteAllText($OutFile, [string]$r)
            if ($Mode -eq 'exit') { $form.Close() }
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    private const string SavePromptScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'RevitAgent Test Host'
        $form.Width = 520; $form.Height = 320
        $form.Add_FormClosing({ param($s, $e)
            if ($Mode -eq 'ignoreclose') { $e.Cancel = $true; return }
            $r = [System.Windows.Forms.MessageBox]::Show($form, $Text, $Title, 'YesNoCancel')
            [IO.File]::WriteAllText($OutFile, [string]$r)
            if ($r -eq 'Cancel') { $e.Cancel = $true }
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    private const string TaskDialogScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        $manifest = [IO.Path]::ChangeExtension($OutFile, '.manifest')
        [IO.File]::WriteAllText($manifest, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><dependency><dependentAssembly><assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/></dependentAssembly></dependency></assembly>')
        Add-Type -TypeDefinition @"
        using System; using System.Runtime.InteropServices;
        public static class RaTestTaskDialog {
          [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
          struct ACTCTX { public int cbSize; public uint dwFlags; public string lpSource; public ushort wProcessorArchitecture; public ushort wLangId; public string lpAssemblyDirectory; public string lpResourceName; public string lpApplicationName; public IntPtr hModule; }
          [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateActCtx(ref ACTCTX a);
          [DllImport("kernel32.dll", SetLastError = true)] static extern bool ActivateActCtx(IntPtr h, out IntPtr cookie);
          [DllImport("comctl32.dll", CharSet = CharSet.Unicode, EntryPoint = "TaskDialog")]
          static extern int TaskDialogNative(IntPtr owner, IntPtr inst, string title, string main, string content, int buttons, IntPtr icon, out int pressed);
          public static int Show(string manifest, string title, string main, string content) {
            var a = new ACTCTX(); a.cbSize = Marshal.SizeOf(typeof(ACTCTX)); a.lpSource = manifest;
            IntPtr h = CreateActCtx(ref a); IntPtr cookie;
            if (h == new IntPtr(-1)) throw new Exception("CreateActCtx " + Marshal.GetLastWin32Error());
            if (!ActivateActCtx(h, out cookie)) throw new Exception("ActivateActCtx " + Marshal.GetLastWin32Error());
            int pressed; int hr = TaskDialogNative(IntPtr.Zero, IntPtr.Zero, title, main, content, 0x2 | 0x4 | 0x20, IntPtr.Zero, out pressed);
            if (hr != 0) throw new Exception("TaskDialog hr=0x" + hr.ToString("X"));
            return pressed;
          }
        }
        "@
        $r = [RaTestTaskDialog]::Show($manifest, $Title, 'Main instruction of the test', $Text)
        [IO.File]::WriteAllText($OutFile, [string]$r)
        """;

    // TaskDialogIndirect with custom command buttons 1001 / 1002 / 1003 (Always Load / Load Once / Do Not Load), like the
    // "Security - Unsigned Add-In" prompt of Revit. '|' in -Text is a line break. The pressed button id goes to the result file.
    private const string UnsignedAddinScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        $manifest = [IO.Path]::ChangeExtension($OutFile, '.manifest')
        [IO.File]::WriteAllText($manifest, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><dependency><dependentAssembly><assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/></dependentAssembly></dependency></assembly>')
        Add-Type -TypeDefinition @"
        using System; using System.Runtime.InteropServices;
        public static class RaTestUnsignedDialog {
          [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
          struct ACTCTX { public int cbSize; public uint dwFlags; public string lpSource; public ushort wProcessorArchitecture; public ushort wLangId; public string lpAssemblyDirectory; public string lpResourceName; public string lpApplicationName; public IntPtr hModule; }
          [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateActCtx(ref ACTCTX a);
          [DllImport("kernel32.dll", SetLastError = true)] static extern bool ActivateActCtx(IntPtr h, out IntPtr cookie);
          [StructLayout(LayoutKind.Sequential, Pack = 1)]
          struct TDBUTTON { public int id; public IntPtr text; }
          [StructLayout(LayoutKind.Sequential, Pack = 1)]
          struct TDCONFIG {
            public uint cbSize; public IntPtr hwndParent; public IntPtr hInstance; public uint dwFlags; public uint dwCommonButtons;
            public IntPtr pszWindowTitle; public IntPtr hMainIcon; public IntPtr pszMainInstruction; public IntPtr pszContent;
            public uint cButtons; public IntPtr pButtons; public int nDefaultButton; public uint cRadioButtons; public IntPtr pRadioButtons; public int nDefaultRadioButton;
            public IntPtr pszVerificationText; public IntPtr pszExpandedInformation; public IntPtr pszExpandedControlText; public IntPtr pszCollapsedControlText;
            public IntPtr hFooterIcon; public IntPtr pszFooter; public IntPtr pfCallback; public IntPtr lpCallbackData; public uint cxWidth;
          }
          [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
          static extern int TaskDialogIndirect(ref TDCONFIG cfg, out int button, out int radio, out bool verification);
          public static int Show(IntPtr owner, string manifest, string title, string main, string content) {
            var a = new ACTCTX(); a.cbSize = Marshal.SizeOf(typeof(ACTCTX)); a.lpSource = manifest;
            IntPtr h = CreateActCtx(ref a); IntPtr cookie;
            if (h == new IntPtr(-1)) throw new Exception("CreateActCtx " + Marshal.GetLastWin32Error());
            if (!ActivateActCtx(h, out cookie)) throw new Exception("ActivateActCtx " + Marshal.GetLastWin32Error());
            string[] labels = new string[] { "Always Load", "Load Once", "Do Not Load" };
            int size = Marshal.SizeOf(typeof(TDBUTTON));
            IntPtr arr = Marshal.AllocHGlobal(size * 3);
            for (int i = 0; i < 3; i++) {
              TDBUTTON b = new TDBUTTON(); b.id = 1001 + i; b.text = Marshal.StringToHGlobalUni(labels[i]);
              Marshal.StructureToPtr(b, new IntPtr(arr.ToInt64() + size * i), false);
            }
            TDCONFIG cfg = new TDCONFIG();
            cfg.cbSize = (uint)Marshal.SizeOf(typeof(TDCONFIG));
            cfg.hwndParent = owner;
            cfg.pszWindowTitle = Marshal.StringToHGlobalUni(title);
            cfg.pszMainInstruction = Marshal.StringToHGlobalUni(main);
            cfg.pszContent = Marshal.StringToHGlobalUni(content);
            cfg.cButtons = 3; cfg.pButtons = arr; cfg.nDefaultButton = 1003;
            int pressed, radio; bool verif;
            int hr = TaskDialogIndirect(ref cfg, out pressed, out radio, out verif);
            if (hr != 0) throw new Exception("TaskDialogIndirect hr=0x" + hr.ToString("X"));
            return pressed;
          }
        }
        "@
        # Owned by a form (main window disabled and kept open afterwards), like Revit's main window.
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'RevitAgent Test Host'
        $form.Width = 520; $form.Height = 320
        $form.Add_Shown({
            $r = [RaTestUnsignedDialog]::Show($form.Handle, $manifest, $Title, 'An add-in is not signed', $Text.Replace('|', "`r`n"))
            [IO.File]::WriteAllText($OutFile, [string]$r)
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    // WinForms main form + a modal WinForms form (ShowDialog disables the owner; not #32770, so a "modal_window" blocker) with a
    // label and Win32 buttons from -Buttons ('|'-separated; a leading '!' makes that button disabled), like Revit's Review Warnings.
    // The pressed label goes to the result file and closes the modal form; the main form stays open (closes on WM_CLOSE).
    private const string ModalFormScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'RevitAgent Test Host'
        $form.Width = 520; $form.Height = 320
        $form.Add_Shown({
            $script:dlg = New-Object System.Windows.Forms.Form
            $script:dlg.Text = $Title
            $script:dlg.Width = 720; $script:dlg.Height = 220
            $label = New-Object System.Windows.Forms.Label
            $label.Text = $Text; $label.Left = 10; $label.Top = 10; $label.Width = 680; $label.Height = 40
            $script:dlg.Controls.Add($label)
            $x = 10
            foreach ($b in $Buttons.Split('|')) {
                $button = New-Object System.Windows.Forms.Button
                $button.Text = $b.TrimStart('!')
                $button.Enabled = -not $b.StartsWith('!')
                $button.Left = $x; $button.Top = 120; $button.Width = 110
                $button.Add_Click({ param($s, $e) [IO.File]::WriteAllText($OutFile, $s.Text); $script:dlg.Close() })
                $script:dlg.Controls.Add($button)
                $x += 115
            }
            [void]$script:dlg.ShowDialog($form)
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    // A form that, when closed (WM_CLOSE), first shows a comctl32 TaskDialog with COMMAND LINKS from -Buttons ('|'-separated,
    // ids 1001, 1002, ...), owned by the form — like an add-in's "update available" prompt (Để sau / Cập nhật) on Revit exit.
    // The pressed id goes to the result file; the form then closes whatever was pressed.
    private const string CloseCommandLinksScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        $manifest = [IO.Path]::ChangeExtension($OutFile, '.manifest')
        [IO.File]::WriteAllText($manifest, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><dependency><dependentAssembly><assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/></dependentAssembly></dependency></assembly>')
        Add-Type -TypeDefinition @"
        using System; using System.Runtime.InteropServices;
        public static class RaTestCommandLinks {
          [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
          struct ACTCTX { public int cbSize; public uint dwFlags; public string lpSource; public ushort wProcessorArchitecture; public ushort wLangId; public string lpAssemblyDirectory; public string lpResourceName; public string lpApplicationName; public IntPtr hModule; }
          [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateActCtx(ref ACTCTX a);
          [DllImport("kernel32.dll", SetLastError = true)] static extern bool ActivateActCtx(IntPtr h, out IntPtr cookie);
          [StructLayout(LayoutKind.Sequential, Pack = 1)]
          struct TDBUTTON { public int id; public IntPtr text; }
          [StructLayout(LayoutKind.Sequential, Pack = 1)]
          struct TDCONFIG {
            public uint cbSize; public IntPtr hwndParent; public IntPtr hInstance; public uint dwFlags; public uint dwCommonButtons;
            public IntPtr pszWindowTitle; public IntPtr hMainIcon; public IntPtr pszMainInstruction; public IntPtr pszContent;
            public uint cButtons; public IntPtr pButtons; public int nDefaultButton; public uint cRadioButtons; public IntPtr pRadioButtons; public int nDefaultRadioButton;
            public IntPtr pszVerificationText; public IntPtr pszExpandedInformation; public IntPtr pszExpandedControlText; public IntPtr pszCollapsedControlText;
            public IntPtr hFooterIcon; public IntPtr pszFooter; public IntPtr pfCallback; public IntPtr lpCallbackData; public uint cxWidth;
          }
          [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
          static extern int TaskDialogIndirect(ref TDCONFIG cfg, out int button, out int radio, out bool verification);
          public static int Show(IntPtr owner, string manifest, string title, string main, string content, string[] labels) {
            var a = new ACTCTX(); a.cbSize = Marshal.SizeOf(typeof(ACTCTX)); a.lpSource = manifest;
            IntPtr h = CreateActCtx(ref a); IntPtr cookie;
            if (h == new IntPtr(-1)) throw new Exception("CreateActCtx " + Marshal.GetLastWin32Error());
            if (!ActivateActCtx(h, out cookie)) throw new Exception("ActivateActCtx " + Marshal.GetLastWin32Error());
            int size = Marshal.SizeOf(typeof(TDBUTTON));
            IntPtr arr = Marshal.AllocHGlobal(size * labels.Length);
            for (int i = 0; i < labels.Length; i++) {
              TDBUTTON b = new TDBUTTON(); b.id = 1001 + i; b.text = Marshal.StringToHGlobalUni(labels[i]);
              Marshal.StructureToPtr(b, new IntPtr(arr.ToInt64() + size * i), false);
            }
            TDCONFIG cfg = new TDCONFIG();
            cfg.cbSize = (uint)Marshal.SizeOf(typeof(TDCONFIG));
            cfg.hwndParent = owner;
            cfg.dwFlags = 0x10; // TDF_USE_COMMAND_LINKS
            cfg.pszWindowTitle = Marshal.StringToHGlobalUni(title);
            cfg.pszMainInstruction = Marshal.StringToHGlobalUni(main);
            cfg.pszContent = Marshal.StringToHGlobalUni(content);
            cfg.cButtons = (uint)labels.Length; cfg.pButtons = arr; cfg.nDefaultButton = 1001;
            int pressed, radio; bool verif;
            int hr = TaskDialogIndirect(ref cfg, out pressed, out radio, out verif);
            if (hr != 0) throw new Exception("TaskDialogIndirect hr=0x" + hr.ToString("X"));
            return pressed;
          }
        }
        "@
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'RevitAgent Test Host'
        $form.Width = 520; $form.Height = 320
        $form.Add_FormClosing({ param($s, $e)
            $r = [RaTestCommandLinks]::Show($form.Handle, $manifest, $Title, 'Update available', $Text, $Buttons.Split('|'))
            [IO.File]::WriteAllText($OutFile, [string]$r)
        })
        [System.Windows.Forms.Application]::Run($form)
        """;

    private const string SleepScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        Start-Sleep -Seconds 120
        """;

    // WPF main window + WPF modal window (ShowDialog disables the owner; no #32770, buttons have no HWND),
    // like most Revit add-in dialogs. Buttons: Apply (AutomationId btnApply) and Cancel (btnCancel).
    private const string WpfModalScript = """
        param([string]$Title, [string]$Text, [string]$Buttons, [string]$OutFile, [string]$Mode)
        Add-Type -AssemblyName PresentationFramework
        $script:main = New-Object System.Windows.Window
        $script:main.Title = 'RevitAgent Test Host'
        $script:main.Width = 520; $script:main.Height = 320
        $mainText = New-Object System.Windows.Controls.TextBlock
        $mainText.Text = 'main window'
        $script:main.Content = $mainText  # ContentRendered only fires for a window with content
        $script:main.Add_ContentRendered({
            $script:dlg = New-Object System.Windows.Window
            $script:dlg.Title = $Title
            $script:dlg.Width = 360; $script:dlg.Height = 200
            $script:dlg.Owner = $script:main
            $panel = New-Object System.Windows.Controls.StackPanel
            $label = New-Object System.Windows.Controls.TextBlock
            $label.Text = $Text
            $apply = New-Object System.Windows.Controls.Button
            $apply.Content = 'Apply'; $apply.Name = 'btnApply'
            $apply.Add_Click({ [IO.File]::WriteAllText($OutFile, 'Apply'); $script:dlg.Close() })
            $cancel = New-Object System.Windows.Controls.Button
            $cancel.Content = 'Cancel'; $cancel.Name = 'btnCancel'
            $cancel.Add_Click({ [IO.File]::WriteAllText($OutFile, 'Cancel'); $script:dlg.Close() })
            [void]$panel.Children.Add($label); [void]$panel.Children.Add($apply); [void]$panel.Children.Add($cancel)
            $script:dlg.Content = $panel
            [void]$script:dlg.ShowDialog()
        })
        $app = New-Object System.Windows.Application
        [void]$app.Run($script:main)
        """;

    private readonly string _dir;

    private DialogHostProcess(string script, string title, string text, string buttons, string mode)
    {
        _dir = TestGateway.NewTempDir("revitagent_dlg_");
        var scriptPath = Path.Combine(_dir, "host.ps1");
        File.WriteAllText(scriptPath, script);
        ResultFile = Path.Combine(_dir, "result.txt");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Title", title, "-Text", text, "-Buttons", buttons, "-OutFile", ResultFile, "-Mode", mode })
        {
            psi.ArgumentList.Add(a);
        }

        Process = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe did not start");
    }

    public Process Process { get; }

    public int Pid => Process.Id;

    public string ResultFile { get; }

    /// <summary>MessageBox owned by a form. mode "stay": the form stays open after the answer; "exit": it closes.</summary>
    public static DialogHostProcess MessageBox(string title, string text, string buttons = "YesNoCancel", string mode = "stay") =>
        new(MessageBoxScript, title, text, buttons, mode);

    /// <summary>comctl32 v6 TaskDialog with Yes / No / Close common buttons (ids 6 / 7 / 8).</summary>
    public static DialogHostProcess TaskDialog(string title, string text) => new(TaskDialogScript, title, text, "none", "none");

    /// <summary>TaskDialog with custom buttons 1001 Always Load / 1002 Load Once / 1003 Do Not Load; '|' in <paramref name="text"/> is a line break. Result: the pressed id.</summary>
    public static DialogHostProcess UnsignedAddin(string title, string text) => new(UnsignedAddinScript, title, text, "none", "none");

    /// <summary>A form that asks "save changes?" (YesNoCancel) when closed; mode "ignoreclose" refuses to close silently.</summary>
    public static DialogHostProcess SavePromptForm(string title, string text, string mode = "prompt") => new(SavePromptScript, title, text, "none", mode);

    /// <summary>WPF main window with a WPF modal window (Apply / Cancel buttons, no Win32 child controls).</summary>
    public static DialogHostProcess WpfModal(string title, string text) => new(WpfModalScript, title, text, "none", "none");

    /// <summary>
    /// Main form + modal WinForms form (a "modal_window" blocker) with <paramref name="buttons"/> ('|'-separated, '!' prefix = disabled).
    /// Result: the pressed label.
    /// </summary>
    public static DialogHostProcess ModalForm(string title, string text, string buttons) => new(ModalFormScript, title, text, buttons, "none");

    /// <summary>A form that shows a command-link TaskDialog (<paramref name="buttons"/> '|'-separated, ids 1001...) when it is closed. Result: the pressed id.</summary>
    public static DialogHostProcess CloseCommandLinks(string title, string text, string buttons) => new(CloseCommandLinksScript, title, text, buttons, "none");

    /// <summary>A process without any window.</summary>
    public static DialogHostProcess Sleeper() => new(SleepScript, "none", "none", "none", "none");

    /// <summary>Waits for a visible top-level window of this process with the given title (and class when given).</summary>
    public WindowInfo WaitForWindow(string title, string? className = null, int timeoutSec = 45)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
        {
            if (Process.HasExited)
            {
                throw new InvalidOperationException($"Helper process exited early (code {Process.ExitCode}).");
            }

            var w = Win32Windows.TopLevelWindows(Pid).FirstOrDefault(x => x.Title == title && (className is null || x.ClassName == className));
            if (w is not null)
            {
                Thread.Sleep(300); // let the dialog finish initializing its controls
                return Win32Windows.Describe(w.Hwnd) ?? w;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"Window '{title}' of helper pid {Pid} did not appear within {timeoutSec} s.");
    }

    public WindowInfo WaitForDialog(string title, int timeoutSec = 45) => WaitForWindow(title, Win32Windows.DialogClass, timeoutSec);

    /// <summary>The button value the dialog returned (null when nothing was answered within the timeout).</summary>
    public string? WaitForResult(int timeoutSec = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
        {
            if (File.Exists(ResultFile))
            {
                try
                {
                    return File.ReadAllText(ResultFile).Trim();
                }
                catch (IOException)
                {
                    // Being written.
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }

    public bool HasExited => Process.HasExited;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(10000);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        Process.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; best effort.
        }
    }
}
