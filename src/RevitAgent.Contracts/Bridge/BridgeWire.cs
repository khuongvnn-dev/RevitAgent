namespace RevitAgent.Contracts.Bridge
{
    /// <summary>
    /// Constants shared by the Host (gateway) and the in-Revit Bridge add-in.
    /// Pure BCL: this file is compiled into Contracts AND linked as source into the Bridge, which must
    /// not reference Contracts or System.Text.Json (Revit loads many add-ins; private copies of common
    /// libraries conflict). Keep it free of dependencies.
    /// </summary>
    public static class BridgeWire
    {
        /// <summary>Wire protocol version reported by <c>ping</c>. Bump on breaking changes.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Stable AddInId of the v2 Bridge (manifest + CodeSigning trust value name, lowercased).</summary>
        public const string AddInId = "2B0C5AC5-295C-415B-BAAD-F731A61C66D8";

        /// <summary>AddInId of the retired v1 Bridge manifest (removed by the installer when found).</summary>
        public const string LegacyAddInId = "B62FE48B-C998-4B81-912A-18A811440C92";

        public const string VendorId = "RVAG";

        public const string AddInName = "RevitAgent Bridge";

        public const string ManifestFileName = "RevitAgent.Bridge.addin";

        public const string AssemblyFileName = "RevitAgent.Bridge.dll";

        public const string FullClassName = "RevitAgent.Bridge.BridgeApplication";

        /// <summary>Default execution budget when a request carries <c>timeout_sec</c> = 0.</summary>
        public const int DefaultTimeoutSec = 60;

        /// <summary>Upper bound for <c>timeout_sec</c>.</summary>
        public const int MaxTimeoutSec = 3600;

        /// <summary>Largest accepted frame payload (bytes).</summary>
        public const int MaxPayloadBytes = 32 * 1024 * 1024;
    }

    /// <summary>
    /// Tool names understood by the Bridge. The Host-side gateway tools with the same names forward to it
    /// (see RevitAgent.Core.Bridge.BridgeToolCatalog). <see cref="Ping"/> is internal (bridge_status uses it).
    /// </summary>
    public static class BridgeToolNames
    {
        public const string Ping = "ping";
        public const string DocInfo = "doc_info";
        public const string DocListOpen = "doc_list_open";
        public const string DocOpen = "doc_open";
        public const string DocClose = "doc_close";
        public const string ModelListCategories = "model_list_categories";
        public const string ModelQueryElements = "model_query_elements";
        public const string ModelGetElement = "model_get_element";
        public const string ModelListViews = "model_list_views";
        public const string ModelGetWarnings = "model_get_warnings";
        public const string ScheduleListSchedulableCategories = "schedule_list_schedulable_categories";
        public const string ScheduleRead = "schedule_read";
        public const string ScheduleCreate = "schedule_create";
        public const string RibbonListItems = "ribbon_list_items";
        public const string CommandListPostable = "command_list_postable";
        public const string CommandPost = "command_post";
        public const string ViewActivate = "view_activate";
        public const string SelectionGet = "selection_get";
        public const string SelectionSet = "selection_set";
        public const string DialogListEvents = "dialog_list_events";

        /// <summary>Every forwarded tool (excludes <see cref="Ping"/>).</summary>
        public static readonly string[] Forwarded =
        {
            DocInfo, DocListOpen, DocOpen, DocClose,
            ModelListCategories, ModelQueryElements, ModelGetElement, ModelListViews, ModelGetWarnings,
            ScheduleListSchedulableCategories, ScheduleRead, ScheduleCreate,
            RibbonListItems, CommandListPostable, CommandPost,
            ViewActivate, SelectionGet, SelectionSet, DialogListEvents,
        };
    }
}
