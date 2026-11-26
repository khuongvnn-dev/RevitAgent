namespace RevitAgent.Contracts.Protocol
{
    /// <summary>
    /// Error codes every gateway tool and the Bridge return in <c>{code, message, details?}</c>.
    /// The gateway's own list of machine-readable error codes.
    /// Messages must tell the agent what to do next; never include stack traces or secrets.
    /// </summary>
    public static class GatewayErrorCodes
    {
        /// <summary>Arguments missing, wrong type, or out of range.</summary>
        public const string ValidationFailed = "VALIDATION_FAILED";

        /// <summary>Target (process, window, element, document, view) does not exist.</summary>
        public const string NotFound = "NOT_FOUND";

        /// <summary>Operation conflicts with current state (e.g. already exists).</summary>
        public const string Conflict = "CONFLICT";

        /// <summary>Blocked by policy, permission level, or a forbidden dialog category.</summary>
        public const string PermissionDenied = "PERMISSION_DENIED";

        /// <summary>Revit not ready: no document, still starting, or a modal dialog is up.</summary>
        public const string TargetNotReady = "TARGET_NOT_READY";

        /// <summary>Another call is running on the same target; retry later.</summary>
        public const string Busy = "BUSY";

        /// <summary>The call exceeded its time budget. The work may still be running in Revit.</summary>
        public const string Timeout = "TIMEOUT";

        /// <summary>A dependency failed (Bridge not loaded / pipe down, UI Automation unavailable, session locked).</summary>
        public const string DependencyFailed = "DEPENDENCY_FAILED";

        /// <summary>A write would touch a document/file outside the gateway sandbox.</summary>
        public const string SandboxViolation = "SANDBOX_VIOLATION";

        /// <summary>A modal dialog blocks the target process; inspect with revit_list_dialogs.</summary>
        public const string ModalBlocked = "MODAL_BLOCKED";

        /// <summary>Unexpected failure; details are in the gateway log, not in the message.</summary>
        public const string Internal = "INTERNAL";
    }
}
