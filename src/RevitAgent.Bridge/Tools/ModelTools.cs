namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Autodesk.Revit.DB;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>model_list_categories, model_query_elements, model_get_element, model_list_views, model_get_warnings.</summary>
    internal static class ModelTools
    {
        private static readonly string[] ClassNamespaces =
        {
            "Autodesk.Revit.DB.", "Autodesk.Revit.DB.Architecture.", "Autodesk.Revit.DB.Mechanical.", "Autodesk.Revit.DB.Plumbing.",
            "Autodesk.Revit.DB.Electrical.", "Autodesk.Revit.DB.Structure.", "Autodesk.Revit.DB.Analysis.",
        };

        private static readonly Dictionary<string, ViewType[]> ViewKinds = new Dictionary<string, ViewType[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "floor_plan", new[] { ViewType.FloorPlan } },
            { "ceiling_plan", new[] { ViewType.CeilingPlan } },
            { "structural_plan", new[] { ViewType.EngineeringPlan } },
            { "area_plan", new[] { ViewType.AreaPlan } },
            { "section", new[] { ViewType.Section } },
            { "detail", new[] { ViewType.Detail } },
            { "elevation", new[] { ViewType.Elevation } },
            { "3d", new[] { ViewType.ThreeD } },
            { "schedule", new[] { ViewType.Schedule, ViewType.ColumnSchedule, ViewType.PanelSchedule } },
            { "sheet", new[] { ViewType.DrawingSheet } },
            { "legend", new[] { ViewType.Legend } },
            { "drafting", new[] { ViewType.DraftingView } },
            { "walkthrough", new[] { ViewType.Walkthrough } },
            { "rendering", new[] { ViewType.Rendering } },
            { "report", new[] { ViewType.Report, ViewType.CostReport, ViewType.LoadsReport, ViewType.PresureLossReport } },
        };

        private static readonly HashSet<ViewType> HiddenViewTypes = new HashSet<ViewType>
        {
            ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal, ViewType.Undefined,
        };

        public static object? ListCategories(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var nameContains = args.Str("name_contains");
            var typeFilter = args.Str("type");
            var limit = args.Limit();
            var schedulable = new HashSet<long>(ViewSchedule.GetValidCategoriesForSchedule().Select(Ids.Of));
            var all = new List<WireObject>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (nameContains != null && c.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var type = c.CategoryType.ToString().ToLowerInvariant();
                if (typeFilter != null && !string.Equals(type, typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, typeFilter.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = Ids.Of(c.Id);
                all.Add(new WireObject
                {
                    { "id", id },
                    { "name", c.Name },
                    { "builtin", Ids.BuiltInCategoryName(c.Id) },
                    { "type", type },
                    { "schedulable", schedulable.Contains(id) },
                    { "allows_bound_parameters", c.AllowsBoundParameters },
                    { "subcategory_count", c.SubCategories?.Size ?? 0 },
                });
            }

            all.Sort((a, b) => string.Compare((string?)a["name"], (string?)b["name"], StringComparison.OrdinalIgnoreCase));
            return new WireObject
            {
                { "total", all.Count },
                { "returned", Math.Min(limit, all.Count) },
                { "categories", all.Take(limit).ToList() },
            };
        }

        public static object? QueryElements(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var categoryKey = args.Str("category");
            var className = args.Str("class");
            if (categoryKey == null && className == null)
            {
                throw ToolArgs.Invalid("Pass 'category' (e.g. OST_Levels or a display name like 'Walls') and/or 'class' (e.g. Wall, FamilyInstance). Category names are OST_* values such as OST_Walls.");
            }

            var limit = args.Limit();
            var offset = args.Offset();
            var includeTypes = args.Bool("include_types", false);
            var fields = args.List("fields").Select(WireValues.AsString).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
            var filters = ParseFilters(args);

            var viewIdArg = args.Long("view_id");
            var collector = viewIdArg.HasValue ? new FilteredElementCollector(doc, Ids.Make(viewIdArg.Value)) : new FilteredElementCollector(doc);
            WireObject? categoryInfo = null;
            if (categoryKey != null)
            {
                var (categoryId, info) = ResolveCategory(doc, categoryKey);
                categoryInfo = info;
                collector = collector.OfCategoryId(categoryId);
            }

            Type? postFilterType = null;
            if (className != null)
            {
                var type = ResolveElementClass(className) ?? throw new BridgeToolException(
                    GatewayErrorCodes.NotFound,
                    "Unknown element class '" + className + "'. Use a Revit API class name such as Wall, Floor, FamilyInstance, Level, Grid, View, ViewSchedule, Room.");
                try
                {
                    collector = collector.OfClass(type);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    postFilterType = type; // e.g. Room: not a native OfClass type, filter in memory
                }
            }

            if (!includeTypes)
            {
                collector = collector.WhereElementIsNotElementType();
            }

            var total = 0;
            var items = new List<object?>();
            foreach (var e in collector)
            {
                if (postFilterType != null && !postFilterType.IsInstanceOfType(e))
                {
                    continue;
                }

                if (!filters.All(f => f.Matches(e, doc)))
                {
                    continue;
                }

                total++;
                if (total <= offset || items.Count >= limit)
                {
                    continue;
                }

                var o = Describe.Brief(e);
                var typeId = e.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    o["type_id"] = Ids.Of(typeId);
                }

                if (fields.Count > 0)
                {
                    var values = new WireObject();
                    foreach (var f in fields)
                    {
                        values[f] = FieldValue(e, f, doc);
                    }

                    o["fields"] = values;
                }

                items.Add(o);
            }

            var result = new WireObject
            {
                { "total", total },
                { "offset", offset },
                { "returned", items.Count },
                { "truncated", offset + items.Count < total },
                { "elements", items },
            };
            if (categoryInfo != null)
            {
                result["category"] = categoryInfo;
            }

            if (fields.Count > 0)
            {
                result["units_note"] = "fields.*.value doubles are Revit internal units (feet); display/display_number use project units.";
            }

            return result;
        }

        public static object? GetElement(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var id = args.Long("id");
            var uniqueId = args.Str("unique_id");
            Element? e;
            if (id.HasValue)
            {
                e = doc.GetElement(Ids.Make(id.Value));
            }
            else if (uniqueId != null)
            {
                e = doc.GetElement(uniqueId);
            }
            else
            {
                throw ToolArgs.Invalid("Pass 'id' (integer element id) or 'unique_id'.");
            }

            if (e == null)
            {
                throw new BridgeToolException(GatewayErrorCodes.NotFound, "No element " + (id.HasValue ? "with id " + id.Value : "with unique_id '" + uniqueId + "'") + " in '" + doc.Title + "'.");
            }

            var o = Describe.Brief(e);
            o["category"] = Describe.Category(e.Category);
            var typeId = e.GetTypeId();
            Element? typeElement = null;
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                typeElement = doc.GetElement(typeId);
                o["type"] = new WireObject { { "id", Ids.Of(typeId) }, { "name", typeElement == null ? null : Describe.SafeName(typeElement) } };
            }

            if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
            {
                o["level"] = new WireObject { { "id", Ids.Of(e.LevelId) }, { "name", doc.GetElement(e.LevelId) is Element lvl ? Describe.SafeName(lvl) : null } };
            }

            if (e.OwnerViewId != null && e.OwnerViewId != ElementId.InvalidElementId)
            {
                o["owner_view_id"] = Ids.Of(e.OwnerViewId);
            }

            if (doc.IsWorkshared)
            {
                try
                {
                    o["workset"] = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name;
                }
                catch (Exception)
                {
                }
            }

            o["location"] = Location(e);
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    o["bounding_box"] = new WireObject { { "min", Describe.Xyz(bb.Min) }, { "max", Describe.Xyz(bb.Max) } };
                }
            }
            catch (Exception)
            {
            }

            o["parameters"] = Parameters(e, doc);
            if (args.Bool("include_type_parameters", false) && typeElement != null)
            {
                o["type_parameters"] = Parameters(typeElement, doc);
            }

            o["units_note"] = "Coordinates and raw double values are Revit internal units (feet); 'display' uses project units.";
            return o;
        }

        public static object? ListViews(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var typeKey = args.Str("type");
            ViewType[]? wanted = null;
            if (typeKey != null && !ViewKinds.TryGetValue(typeKey, out wanted))
            {
                throw ToolArgs.Invalid("Unknown view type '" + typeKey + "'. Use one of: " + string.Join(", ", ViewKinds.Keys) + ".");
            }

            var nameContains = args.Str("name_contains");
            var includeTemplates = args.Bool("include_templates", false);
            var limit = args.Limit();
            var activeId = ctx.IsActive(doc) ? ctx.App.ActiveUIDocument?.ActiveView?.Id : null;
            var all = new List<WireObject>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (v.IsTemplate && !includeTemplates)
                {
                    continue;
                }

                if (wanted != null ? Array.IndexOf(wanted, v.ViewType) < 0 : HiddenViewTypes.Contains(v.ViewType))
                {
                    continue;
                }

                if (v is ViewSchedule vs && (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule))
                {
                    continue;
                }

                if (nameContains != null && v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var o = new WireObject
                {
                    { "id", Ids.Of(v.Id) },
                    { "name", v.Name },
                    { "kind", KindOf(v.ViewType) },
                    { "view_type", v.ViewType.ToString() },
                    { "is_template", v.IsTemplate },
                    { "is_active", activeId != null && v.Id == activeId },
                };
                if (v.GenLevel != null)
                {
                    o["level"] = v.GenLevel.Name;
                }

                if (v is ViewSheet sheet)
                {
                    o["sheet_number"] = sheet.SheetNumber;
                }

                all.Add(o);
            }

            all.Sort((a, b) =>
            {
                var k = string.Compare((string?)a["kind"], (string?)b["kind"], StringComparison.Ordinal);
                return k != 0 ? k : string.Compare((string?)a["name"], (string?)b["name"], StringComparison.OrdinalIgnoreCase);
            });
            return new WireObject
            {
                { "total", all.Count },
                { "returned", Math.Min(limit, all.Count) },
                { "views", all.Take(limit).ToList() },
            };
        }

        public static object? GetWarnings(ToolContext ctx)
        {
            var doc = ctx.Document();
            var limit = ctx.Args.Limit();
            var warnings = doc.GetWarnings();
            var items = new List<object?>();
            foreach (var w in warnings)
            {
                if (items.Count >= limit)
                {
                    break;
                }

                items.Add(new WireObject
                {
                    { "description", w.GetDescriptionText() },
                    { "severity", w.GetSeverity().ToString() },
                    { "failing_ids", w.GetFailingElements().Select(Ids.Of).ToList() },
                    { "additional_ids", w.GetAdditionalElements().Select(Ids.Of).ToList() },
                    { "definition_guid", w.GetFailureDefinitionId()?.Guid.ToString() },
                });
            }

            return new WireObject { { "total", warnings.Count }, { "returned", items.Count }, { "warnings", items } };
        }

        /// <summary>Resolves OST_* names (BuiltInCategory) or category display names.</summary>
        public static (ElementId Id, WireObject Info) ResolveCategory(Document doc, string key)
        {
            if (key.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse(key, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    Category? cat = null;
                    try
                    {
                        cat = Category.GetCategory(doc, bic);
                    }
                    catch (Exception)
                    {
                    }

                    var id = cat?.Id ?? Ids.Make(Convert.ToInt64(bic, CultureInfo.InvariantCulture));
                    return (id, new WireObject { { "id", Ids.Of(id) }, { "name", cat?.Name }, { "builtin", bic.ToString() } });
                }

                throw new BridgeToolException(GatewayErrorCodes.NotFound, "Unknown BuiltInCategory '" + key + "'. Pass an OST_* name such as OST_Walls or a category display name.");
            }

            foreach (Category c in doc.Settings.Categories)
            {
                if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return (c.Id, Describe.Category(c)!);
                }
            }

            throw new BridgeToolException(GatewayErrorCodes.NotFound, "No category named '" + key + "' in this model. Display names are localized: pass a BuiltInCategory such as OST_Walls instead.");
        }

        public static Type? ResolveElementClass(string name)
        {
            var asm = typeof(Element).Assembly;
            if (name.IndexOf('.') >= 0)
            {
                var direct = asm.GetType(name, false, true);
                return direct != null && typeof(Element).IsAssignableFrom(direct) ? direct : null;
            }

            foreach (var ns in ClassNamespaces)
            {
                var t = asm.GetType(ns + name, false, true);
                if (t != null && typeof(Element).IsAssignableFrom(t))
                {
                    return t;
                }
            }

            return null;
        }

        public static string KindOf(ViewType t)
        {
            foreach (var kv in ViewKinds)
            {
                if (Array.IndexOf(kv.Value, t) >= 0)
                {
                    return kv.Key;
                }
            }

            return "other";
        }

        private static object? FieldValue(Element e, string name, Document doc)
        {
            var p = ParameterReader.Lookup(e, name);
            if (p != null)
            {
                return ParameterReader.Field(p, doc);
            }

            if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase))
            {
                var n = Describe.SafeName(e);
                return new WireObject { { "value", n }, { "display", n }, { "storage", "string" } };
            }

            return null;
        }

        private static List<object?> Parameters(Element e, Document doc)
        {
            var list = new List<WireObject>();
            foreach (Parameter p in e.Parameters)
            {
                try
                {
                    list.Add(ParameterReader.Full(p, doc));
                }
                catch (Exception)
                {
                    // skip unreadable parameters
                }
            }

            list.Sort((a, b) => string.Compare((string?)a["name"], (string?)b["name"], StringComparison.OrdinalIgnoreCase));
            return list.Cast<object?>().ToList();
        }

        private static WireObject? Location(Element e)
        {
            try
            {
                switch (e.Location)
                {
                    case LocationPoint lp:
                        var point = new WireObject { { "kind", "point" }, { "point", Describe.Xyz(lp.Point) } };
                        try
                        {
                            point["rotation"] = Describe.Round(lp.Rotation);
                        }
                        catch (Exception)
                        {
                        }

                        return point;
                    case LocationCurve lc when lc.Curve != null && lc.Curve.IsBound:
                        return new WireObject
                        {
                            { "kind", "curve" },
                            { "start", Describe.Xyz(lc.Curve.GetEndPoint(0)) },
                            { "end", Describe.Xyz(lc.Curve.GetEndPoint(1)) },
                            { "length", Describe.Round(lc.Curve.Length) },
                        };
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<ParamFilter> ParseFilters(ToolArgs args)
        {
            var result = new List<ParamFilter>();
            foreach (var item in args.List("filters"))
            {
                if (!(item is WireObject f))
                {
                    throw ToolArgs.Invalid("Each entry of 'filters' must be an object {name, op, value}.");
                }

                var name = WireValues.AsString(f["name"]);
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw ToolArgs.Invalid("A filter is missing 'name' (parameter name, e.g. \"Elevation\" or LEVEL_ELEV).");
                }

                var op = (WireValues.AsString(f["op"]) ?? "equals").ToLowerInvariant();
                if (Array.IndexOf(ParamFilter.Ops, op) < 0)
                {
                    throw ToolArgs.Invalid("Filter op '" + op + "' is not supported. Use one of: " + string.Join(", ", ParamFilter.Ops) + ".");
                }

                var value = WireValues.AsString(f["value"]);
                if (value == null && op != "exists")
                {
                    throw ToolArgs.Invalid("Filter on '" + name + "' needs a 'value'.");
                }

                result.Add(new ParamFilter(name!, op, value));
            }

            return result;
        }

        private sealed class ParamFilter
        {
            public static readonly string[] Ops = { "equals", "not_equals", "contains", "gt", "gte", "lt", "lte", "exists" };

            private readonly double? _number;

            public ParamFilter(string name, string op, string? value)
            {
                Name = name;
                Op = op;
                Value = value;
                if (value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    _number = d;
                }

                if ((op == "gt" || op == "gte" || op == "lt" || op == "lte") && _number == null)
                {
                    throw ToolArgs.Invalid("Filter '" + op + "' on '" + name + "' needs a numeric value (got '" + value + "').");
                }
            }

            public string Name { get; }

            public string Op { get; }

            public string? Value { get; }

            public bool Matches(Element e, Document doc)
            {
                var p = ParameterReader.Lookup(e, Name);
                string? text;
                double? number;
                if (p != null)
                {
                    text = ParameterReader.Display(p) ?? WireValues.AsString(ParameterReader.RawValue(p));
                    number = ParameterReader.Number(p, doc);
                    if (Op == "exists")
                    {
                        return p.HasValue;
                    }
                }
                else if (string.Equals(Name, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    text = Describe.SafeName(e);
                    number = null;
                    if (Op == "exists")
                    {
                        return text != null;
                    }
                }
                else
                {
                    return Op == "not_equals";
                }

                switch (Op)
                {
                    case "equals":
                        return EqualsValue(text, number);
                    case "not_equals":
                        return !EqualsValue(text, number);
                    case "contains":
                        return text != null && text.IndexOf(Value!, StringComparison.OrdinalIgnoreCase) >= 0;
                    case "gt":
                        return number.HasValue && number.Value > _number!.Value;
                    case "gte":
                        return number.HasValue && number.Value >= _number!.Value;
                    case "lt":
                        return number.HasValue && number.Value < _number!.Value;
                    case "lte":
                        return number.HasValue && number.Value <= _number!.Value;
                    default:
                        return false;
                }
            }

            private bool EqualsValue(string? text, double? number)
            {
                if (_number.HasValue && number.HasValue && Math.Abs(number.Value - _number.Value) <= 1e-6 * Math.Max(1.0, Math.Abs(_number.Value)))
                {
                    return true;
                }

                return text != null && string.Equals(text.Trim(), Value!.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
