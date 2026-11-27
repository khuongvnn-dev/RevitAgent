namespace RevitAgent.Bridge.Runtime
{
    using System;
    using Autodesk.Revit.ApplicationServices;
    using Autodesk.Revit.DB.Events;
    using RevitAgent.Contracts.Bridge;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// In a Revit the gateway launched with sandbox enforcement on (<see cref="SandboxSaveGuard.FromEnvironment"/>), cancels
    /// every save whose file is outside the sandbox (Save and Save As, also when the UI presses Save) and every synchronize
    /// with central, so an original model can never be written from an agent session. A Revit the user started has no such
    /// environment and is never subscribed: its saves are untouched.
    /// </summary>
    internal sealed class SaveGuardEvents
    {
        private readonly SandboxSaveGuard _guard;
        private readonly IBridgeLog _log;

        private SaveGuardEvents(SandboxSaveGuard guard, IBridgeLog log)
        {
            _guard = guard;
            _log = log;
        }

        /// <summary>Subscribes when this process was launched by the gateway with sandbox enforcement on; otherwise null.</summary>
        public static SaveGuardEvents? TryAttach(ControlledApplication app, IBridgeLog log)
        {
            var guard = SandboxSaveGuard.FromEnvironment(Environment.GetEnvironmentVariable);
            if (guard == null)
            {
                return null;
            }

            var events = new SaveGuardEvents(guard, log);
            app.DocumentSaving += events.OnSaving;
            app.DocumentSavingAs += events.OnSavingAs;
            app.DocumentSynchronizingWithCentral += events.OnSynchronizing;
            log.Info("Save guard on (gateway-launched Revit, sandbox enforcement): saves outside " + string.Join(", ", guard.Roots) + " and sync with central are cancelled");
            return events;
        }

        public void Detach(ControlledApplication app)
        {
            app.DocumentSaving -= OnSaving;
            app.DocumentSavingAs -= OnSavingAs;
            app.DocumentSynchronizingWithCentral -= OnSynchronizing;
        }

        private void OnSaving(object? sender, DocumentSavingEventArgs e)
        {
            string? path = null;
            try
            {
                path = e.Document?.PathName;
                if (string.IsNullOrEmpty(path))
                {
                    return; // never saved: Revit asks for a file name, and DocumentSavingAs checks that target
                }

                if (_guard.SaveRefusal(path) is { } why)
                {
                    Cancel(e, "Save of '" + e.Document?.Title + "' cancelled: " + why);
                }
            }
            catch (Exception ex)
            {
                Cancel(e, "Save of '" + path + "' cancelled: the save guard failed (" + ex.GetType().Name + ")");
            }
        }

        private void OnSavingAs(object? sender, DocumentSavingAsEventArgs e)
        {
            string? path = null;
            try
            {
                path = e.PathName;
                if (_guard.SaveRefusal(path) is { } why)
                {
                    Cancel(e, "Save As of '" + e.Document?.Title + "' cancelled: " + why);
                }
            }
            catch (Exception ex)
            {
                Cancel(e, "Save As to '" + path + "' cancelled: the save guard failed (" + ex.GetType().Name + ")");
            }
        }

        private void OnSynchronizing(object? sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            string? title = null;
            try
            {
                title = e.Document?.Title;
            }
            catch (Exception)
            {
            }

            Cancel(e, "Synchronize with central of '" + title + "' cancelled: a gateway-launched Revit never writes to a central model");
        }

        private void Cancel(RevitAPIPreEventArgs e, string message)
        {
            try
            {
                if (e.Cancellable)
                {
                    e.Cancel();
                    _log.Info(message);
                }
                else
                {
                    _log.Error(message + " — but Revit reports this event as not cancellable");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Save guard could not cancel: " + message, ex);
            }
        }
    }
}
