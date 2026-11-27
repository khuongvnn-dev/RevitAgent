namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Autodesk.Revit.DB;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>view_export_image: renders a view to PNG through the Revit API (independent of what is on screen).</summary>
    internal static class ViewExportTools
    {
        public static object? ExportImage(ToolContext ctx)
        {
            var doc = ctx.Document();
            var width = (int)Math.Min(ctx.Args.Long("pixel_width") ?? 1600, 4096);
            if (width < 64)
            {
                throw ToolArgs.Invalid("'pixel_width' must be between 64 and 4096.");
            }

            var target = ctx.Args.Str("save_path") ?? throw ToolArgs.Invalid("'save_path' is required (the gateway fills it in).");
            target = Path.GetFullPath(target);
            if (!string.Equals(Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolArgs.Invalid("'save_path' must be a .png file.");
            }

            // New files only: an existing file with content (evidence, run data) is never replaced or deleted by an export.
            // An EMPTY file is a name the gateway reserved for this export (EvidenceService.Reserve): it is filled in.
            if (Directory.Exists(target) || (File.Exists(target) && new FileInfo(target).Length > 0))
            {
                throw new BridgeToolException(GatewayErrorCodes.Conflict, "'" + target + "' already exists; view_export_image only writes new files. Pick another save_path.");
            }

            var view = ResolveView(ctx, doc);
            if (view.IsTemplate || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
            {
                throw ToolArgs.Invalid("View '" + view.Name + "' (" + view.ViewType + ") cannot be exported as an image; pick a plan, section, elevation, 3D view, drafting view or sheet.");
            }

            var targetDir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDir);
            var temp = Path.Combine(targetDir, ".export_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(temp, "view"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = width,
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                try
                {
                    doc.ExportImage(options);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "Revit could not render view '" + view.Name + "': " + ex.Message);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Revit refused to export view '" + view.Name + "': " + ex.Message);
                }

                var produced = Directory.GetFiles(temp, "*.png");
                if (produced.Length == 0)
                {
                    throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "Revit produced no image for view '" + view.Name + "' (the view may be empty or not exportable).");
                }

                var source = produced.OrderByDescending(f => new FileInfo(f).Length).First();
                try
                {
                    if (File.Exists(target) && new FileInfo(target).Length == 0)
                    {
                        File.Delete(target); // the empty reservation only (checked just now); never a file with content
                    }

                    File.Move(source, target); // never overwrites: fails when the target appeared meanwhile
                }
                catch (IOException) when (File.Exists(target))
                {
                    throw new BridgeToolException(GatewayErrorCodes.Conflict, "'" + target + "' already exists; view_export_image only writes new files. Pick another save_path.");
                }

                var size = PngSize(target);
                return new WireObject
                {
                    { "path", target },
                    { "width", size.Item1 },
                    { "height", size.Item2 },
                    { "view_id", Ids.Of(view.Id) },
                    { "view_name", view.Name },
                    { "view_type", view.ViewType.ToString() },
                    { "document", doc.Title },
                };
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static View ResolveView(ToolContext ctx, Document doc)
        {
            var id = ctx.Args.Long("view_id");
            if (id.HasValue)
            {
                return doc.GetElement(Ids.Make(id.Value)) as View
                    ?? throw new BridgeToolException(GatewayErrorCodes.NotFound, "Element " + id.Value + " is not a view in '" + doc.Title + "'. Find views with model_query_elements(class: \"View3D\" or \"ViewPlan\" or \"ViewSection\", fields: [\"Name\"]).");
            }

            var name = ctx.Args.Str("name");
            if (name == null)
            {
                var active = ctx.IsActive(doc) ? ctx.App.ActiveUIDocument.ActiveView : doc.ActiveView;
                return active ?? throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "Document '" + doc.Title + "' has no active view; pass view_id or name.");
            }

            var matches = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                throw new BridgeToolException(GatewayErrorCodes.NotFound, "No view named '" + name + "'. Find views with model_query_elements(class: \"View3D\" or \"ViewPlan\" or \"ViewSection\", fields: [\"Name\"]).");
            }

            if (matches.Count > 1)
            {
                throw new BridgeToolException(GatewayErrorCodes.Conflict, matches.Count + " views are named '" + name + "'; pass view_id.",
                    new WireObject { { "views", matches.Select(v => (object?)Describe.View(v)).ToList() } });
            }

            return matches[0];
        }

        private static Tuple<int, int> PngSize(string path)
        {
            using (var s = File.OpenRead(path))
            {
                var b = new byte[24];
                if (s.Read(b, 0, 24) < 24 || b[1] != 0x50)
                {
                    return Tuple.Create(0, 0);
                }

                return Tuple.Create((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            }
        }
    }
}
