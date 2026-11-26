namespace RevitAgent.Bridge.Runtime
{
    using System;
    using System.IO.Pipes;
    using System.Security.AccessControl;
    using System.Security.Principal;

    /// <summary>
    /// Creates instances of the per-process Bridge pipe restricted to the current Windows user
    /// (explicit DACL: current user full control, network logons denied).
    /// </summary>
    internal static class BridgePipeFactory
    {
        /// <summary>Concurrent connections allowed (extra ones exist only so a second caller gets BUSY instead of hanging).</summary>
        public const int MaxInstances = 8;

        public static Func<NamedPipeServerStream> Create(string pipeName)
        {
            var security = BuildSecurity();
            return () =>
#if NETFRAMEWORK
                new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
                NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
        }

        private static PipeSecurity BuildSecurity()
        {
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var user = identity.User ?? throw new InvalidOperationException("Current Windows user has no SID.");
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }
    }
}
