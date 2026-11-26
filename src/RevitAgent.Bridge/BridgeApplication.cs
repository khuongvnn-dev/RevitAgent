namespace RevitAgent.Bridge
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Autodesk.Revit.UI;
    using RevitAgent.Bridge.Runtime;
    using RevitAgent.Bridge.Tools;
    using RevitAgent.Contracts.Bridge;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// RevitAgent Bridge add-in (v2). Always on when loaded: at startup it opens the per-process pipe
    /// <c>\\.\pipe\revitagent-bridge-&lt;pid&gt;</c> (current user only) and serves Bridge tools to the
    /// RevitAgent gateway. It does not change Revit's behaviour: no dialog overrides, no sync blocking,
    /// no failure processing; DialogBoxShowing is only recorded. Startup is fast and never throws.
    /// </summary>
    public sealed class BridgeApplication : IExternalApplication
    {
        private BridgeLog? _log;
        private DialogEventRecorder? _dialogs;
        private RevitExecutor? _executor;
        private BridgeServerCore? _server;

        public Result OnStartup(UIControlledApplication application)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var pid = Process.GetCurrentProcess().Id;
                _log = new BridgeLog(pid);
                var ca = application.ControlledApplication;
                var runtime = new BridgeRuntimeInfo(ca.VersionNumber, ca.VersionName, ca.VersionBuild, ca.SubVersionNumber, _log.FilePath);
                _dialogs = new DialogEventRecorder();
                var registry = new BridgeToolRegistry(runtime, _dialogs);
                _executor = new RevitExecutor(registry, _log);
                _executor.CreateEvent();
                application.DialogBoxShowing += _dialogs.OnDialogBoxShowing;

                var pipeName = BridgePipeNames.ForProcess(pid);
                _server = new BridgeServerCore(pipeName, BridgePipeFactory.Create(pipeName), _executor, _log);
                var server = _server;
                _dialogs.CurrentTool = () => server.BusyTool;
                _server.Start();

                _log.Info("Bridge " + runtime.BridgeVersion + " started in " + runtime.RevitVersionName + " " + runtime.RevitSubVersion + " (" + runtime.RevitVersionBuild + "), " +
                          runtime.Framework + ", pid " + pid + ", pipe \\\\.\\pipe\\" + pipeName + ", assembly " + runtime.AssemblyPath + ", " + sw.ElapsedMilliseconds + " ms");
                var log = _log;
                Task.Run(() => log.CleanupOldLogs());
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Error("Bridge startup failed; the Bridge is unavailable in this Revit session", ex);
                }
                catch (Exception)
                {
                }
            }

            // Always succeed: a failing add-in would make Revit show an error dialog at startup.
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (_dialogs != null)
                {
                    application.DialogBoxShowing -= _dialogs.OnDialogBoxShowing;
                }

                _server?.Stop();
                _executor?.DisposeEvent();
                _log?.Info("Bridge stopped");
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Error("Bridge shutdown failed", ex);
                }
                catch (Exception)
                {
                }
            }

            return Result.Succeeded;
        }
    }
}
