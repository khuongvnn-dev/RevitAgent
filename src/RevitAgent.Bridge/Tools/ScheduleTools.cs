namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Autodesk.Revit.DB;
    using RevitAgent.Contracts.Bridge;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>schedule_list_schedulable_categories, schedule_read, schedule_create.</summary>
    internal static class ScheduleTools
    {
        public const string CreateTransactionName = "RevitAgent: schedule_create";

        private const int MaxListedFields = 30;
        public static object? ListSchedulableCategories(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var nameContains = args.Str("name_contains");
            var limit = args.Limit();
            var valid = ViewSchedule.GetValidCategoriesForSchedule();
            var validIds = new HashSet<long>(valid.Select(Ids.Of));
            var all = new List<WireObject>();
            foreach (var id in valid)
            {
                Category? cat = null;
                try
                {
                    cat = Category.GetCategory(doc, id);
                }
                catch (Exception)
                {
                }

                var name = cat?.Name;
                if (nameContains != null && (name == null || name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                all.Add(new WireObject { { "id", Ids.Of(id) }, { "name", name }, { "builtin", Ids.BuiltInCategoryName(id) } });
            }

            all.Sort((a, b) => string.Compare((string?)a["name"], (string?)b["name"], StringComparison.OrdinalIgnoreCase));
            var result = new WireObject
            {
                { "total", all.Count },
                { "returned", Math.Min(limit, all.Count) },
                { "categories", all.Take(limit).ToList() },
            };

            var check = args.Str("category");
            if (check != null)
            {
                var (catId, info) = ModelTools.ResolveCategory(doc, check);
                var ok = validIds.Contains(Ids.Of(catId));
                result["check"] = new WireObject
                {
                    { "category", info },
                    { "schedulable", ok },
                    {
                        "hint", ok
                            ? "This category can be scheduled: create one with schedule_create."
                            : "Revit cannot create a schedule for this category. Read its elements with model_query_elements instead."
                    },
                };
            }

            return result;
        }

        public static object? Read(ToolContext ctx)
        {
            var doc = ctx.Document();
            var args = ctx.Args;
            var limit = args.Limit("limit", 100, 1000);
            var schedule = Find(doc, args);
            var table = schedule.GetTableData();
            var definition = schedule.Definition;

            var fields = new List<object?>();
            var headings = new List<string?>();
            for (var i = 0; i < definition.GetFieldCount(); i++)
            {
                // GetField(index) follows the field order, which is the column order; hidden fields have no column.
                var f = definition.GetField(i);
                fields.Add(new WireObject
                {
                    { "name", f.GetName() },
                    { "heading", f.ColumnHeading },
                    { "hidden", f.IsHidden },
                    { "field_type", f.FieldType.ToString() },
                });
                if (!f.IsHidden)
                {
                    headings.Add(f.ColumnHeading);
                }
            }

            var result = new WireObject
            {
                { "id", Ids.Of(schedule.Id) },
                { "name", schedule.Name },
                { "category", definition.CategoryId == ElementId.InvalidElementId ? null : Describe.Category(Category.GetCategory(doc, definition.CategoryId)) },
                { "is_itemized", definition.IsItemized },
                { "fields", fields },
                { "header", Title(schedule, table, 50) },
                { "body", Body(schedule, table, headings, definition.ShowHeaders, limit) },
            };
            return result;
        }

        /// <summary>
        /// Creates a regular schedule of one category with the given fields and sort order, in one transaction (any error
        /// rolls everything back), then optionally makes it the active view. The gateway pins <c>document</c> to the full path
        /// of a sandbox copy before forwarding, hence the exact path match (a copy keeps the original's title).
        /// </summary>
        public static object? Create(ToolContext ctx)
        {
            var args = ctx.Args;
            var doc = ctx.Document(exactPath: true);
            var categoryKey = args.ReqStr("category");
            var wanted = Names(args, "fields");
            var sortBy = Names(args, "sort_by");
            var descending = args.Bool("sort_descending", false);
            var name = args.Str("name");
            var activate = args.Bool("activate", true);

            if (wanted.Count == 0)
            {
                throw ToolArgs.Invalid("Pass 'fields': at least one field name of the category.");
            }

            var repeated = FirstRepeated(wanted);
            if (repeated != null)
            {
                throw ToolArgs.Invalid("Field '" + repeated + "' is listed twice in 'fields'.");
            }

            repeated = FirstRepeated(sortBy);
            if (repeated != null)
            {
                throw ToolArgs.Invalid("Field '" + repeated + "' is listed twice in 'sort_by'.");
            }

            foreach (var s in sortBy)
            {
                if (!wanted.Contains(s, StringComparer.OrdinalIgnoreCase))
                {
                    throw ToolArgs.Invalid("sort_by '" + s + "' must also be listed in 'fields'.");
                }
            }

            if (name != null && !NamingUtils.IsValidName(name))
            {
                throw ToolArgs.Invalid("'" + name + "' contains characters Revit does not allow in names.");
            }

            var (categoryId, categoryInfo) = ModelTools.ResolveCategory(doc, categoryKey);
            if (!ViewSchedule.IsValidCategoryForSchedule(categoryId))
            {
                throw ToolArgs.Invalid("Revit cannot schedule category '" + categoryKey + "' (schedule_list_schedulable_categories lists the ones it can). Read its elements with model_query_elements instead.");
            }

            if (name != null)
            {
                // Case-insensitive like schedule_read's name lookup, which would otherwise become ambiguous.
                var clash = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .FirstOrDefault(s => !s.IsTemplate && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (clash != null)
                {
                    throw new BridgeToolException(
                        GatewayErrorCodes.Conflict,
                        "A schedule named '" + clash.Name + "' already exists (view_id " + Ids.Of(clash.Id) + "). Pick another name, or read it with schedule_read.",
                        new WireObject { { "view_id", Ids.Of(clash.Id) } });
                }
            }

            if (activate && !ctx.IsActive(doc))
            {
                throw new BridgeToolException(
                    GatewayErrorCodes.Conflict,
                    "activate=true works on the active document only, and '" + doc.Title + "' is not active. Pass activate=false, or make it the active document first.");
            }

            if (doc.IsReadOnly)
            {
                throw new BridgeToolException(GatewayErrorCodes.TargetNotReady, "'" + doc.Title + "' is read-only right now; no schedule was created.");
            }

            ViewSchedule schedule;
            var fieldsOut = new List<object?>();
            var sortOut = new List<object?>();
            using (var t = new Transaction(doc, CreateTransactionName))
            {
                t.Start();
                try
                {
                    schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                    var definition = schedule.Definition;
                    var available = new List<KeyValuePair<string, SchedulableField>>();
                    foreach (var sf in definition.GetSchedulableFields())
                    {
                        var fieldName = FieldName(sf, doc);
                        if (fieldName != null)
                        {
                            available.Add(new KeyValuePair<string, SchedulableField>(fieldName, sf));
                        }
                    }

                    var added = new Dictionary<string, ScheduleField>(StringComparer.OrdinalIgnoreCase);
                    foreach (var w in wanted)
                    {
                        var match = Match(available, w) ?? throw UnknownField(w, available, categoryInfo);
                        var field = definition.AddField(match);
                        added[w] = field;
                        fieldsOut.Add(new WireObject { { "name", field.GetName() }, { "heading", field.ColumnHeading } });
                    }

                    foreach (var s in sortBy)
                    {
                        var field = added[s];
                        if (!definition.CanSortByField(field.FieldId))
                        {
                            throw ToolArgs.Invalid("Revit cannot sort a schedule by '" + field.GetName() + "'.");
                        }

                        try
                        {
                            definition.AddSortGroupField(new ScheduleSortGroupField(field.FieldId, descending ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending));
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                        {
                            throw ToolArgs.Invalid("Revit refused sort field '" + field.GetName() + "': " + ex.Message);
                        }

                        sortOut.Add(new WireObject { { "field", field.GetName() }, { "descending", descending } });
                    }

                    if (name != null)
                    {
                        try
                        {
                            schedule.Name = name;
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            throw new BridgeToolException(GatewayErrorCodes.Conflict, "Revit refused the name '" + name + "': " + ex.Message);
                        }
                    }

                    var status = t.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new BridgeToolException(
                            GatewayErrorCodes.Conflict,
                            "Revit did not commit the schedule (transaction " + status + "), so nothing was created. Check dialog_list_events.");
                    }
                }
                catch (Exception)
                {
                    if (t.GetStatus() == TransactionStatus.Started)
                    {
                        t.RollBack();
                    }

                    throw;
                }
            }

            // Same mechanism as view_activate, after the commit (the active view cannot change while a transaction is open).
            var activated = false;
            string? activationError = null;
            if (activate)
            {
                try
                {
                    var uidoc = ctx.App.ActiveUIDocument;
                    if (uidoc != null)
                    {
                        uidoc.ActiveView = schedule;
                        activated = true;
                    }
                }
                catch (Exception ex)
                {
                    activationError = ex.Message;
                }
            }

            var result = new WireObject
            {
                { "view_id", Ids.Of(schedule.Id) },
                { "name", schedule.Name },
                { "category", categoryInfo },
                { "fields", fieldsOut },
                { "sort", sortOut },
                { "activated", activated },
            };
            if (activationError != null)
            {
                result["activation_error"] = activationError + " The schedule exists; open it with view_activate.";
            }

            return result;
        }

        private static List<string> Names(ToolArgs args, string key)
        {
            var names = new List<string>();
            foreach (var item in args.List(key))
            {
                var s = item as string;
                if (s == null || s.Trim().Length == 0)
                {
                    throw ToolArgs.Invalid("'" + key + "' must be an array of non-empty field names.");
                }

                names.Add(s.Trim());
            }

            return names;
        }

        private static string? FirstRepeated(List<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return names.FirstOrDefault(n => !seen.Add(n));
        }

        private static string? FieldName(SchedulableField field, Document doc)
        {
            try
            {
                return field.GetName(doc);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Exact name first, then case-insensitive.</summary>
        private static SchedulableField? Match(List<KeyValuePair<string, SchedulableField>> available, string wanted)
        {
            foreach (var a in available)
            {
                if (string.Equals(a.Key, wanted, StringComparison.Ordinal))
                {
                    return a.Value;
                }
            }

            foreach (var a in available)
            {
                if (string.Equals(a.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return a.Value;
                }
            }

            return null;
        }

        /// <summary>VALIDATION_FAILED listing up to 30 field names of the category, those containing the requested text first.</summary>
        private static BridgeToolException UnknownField(string wanted, List<KeyValuePair<string, SchedulableField>> available, WireObject category)
        {
            var names = available.Select(a => a.Key).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = names.Where(n => n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                .Concat(names.Where(n => n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0))
                .Take(MaxListedFields)
                .ToList();
            var categoryName = (category["name"] as string) ?? (category["builtin"] as string) ?? "?";
            return new BridgeToolException(
                GatewayErrorCodes.ValidationFailed,
                "Unknown field '" + wanted + "' for category '" + categoryName + "'. Available (" + shown.Count + " of " + names.Count + "): " + string.Join(", ", shown) + ".",
                new WireObject
                {
                    { "field", wanted },
                    { "available", shown.Cast<object?>().ToList() },
                    { "available_total", names.Count },
                });
        }

        private static ViewSchedule Find(Document doc, ToolArgs args)
        {
            var id = args.Long("view_id");
            if (id.HasValue)
            {
                return doc.GetElement(Ids.Make(id.Value)) as ViewSchedule
                    ?? throw new BridgeToolException(GatewayErrorCodes.NotFound, "Element " + id.Value + " is not a schedule view. Use model_list_views(type=schedule).");
            }

            var name = args.Str("name") ?? throw ToolArgs.Invalid("Pass 'view_id' or 'name' of the schedule (model_list_views with type=schedule lists them).");
            var matches = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new BridgeToolException(GatewayErrorCodes.Conflict, matches.Count + " schedules are named '" + name + "'; pass view_id instead.",
                    new WireObject { { "ids", matches.Select(m => (object?)Ids.Of(m.Id)).ToList() } });
            }

            throw new BridgeToolException(GatewayErrorCodes.NotFound, "No schedule named '" + name + "'. Use model_list_views(type=schedule).");
        }

        /// <summary>The title section (SectionType.Header): every row as text.</summary>
        private static WireObject? Title(ViewSchedule schedule, TableData table, int limit)
        {
            var section = SectionData(table, SectionType.Header);
            if (section == null)
            {
                return null;
            }

            var rows = new List<object?>();
            var rowCount = section.NumberOfRows;
            for (var i = 0; i < rowCount && rows.Count < limit; i++)
            {
                rows.Add(Row(schedule, section, SectionType.Header, i));
            }

            return new WireObject
            {
                { "total_rows", rowCount },
                { "columns", section.NumberOfColumns },
                { "returned_rows", rows.Count },
                { "truncated", rows.Count < rowCount },
                { "rows", rows },
            };
        }

        /// <summary>
        /// The body section split into its header (grouped-header captions, column headings, spacer row: see
        /// <see cref="ScheduleHeader.CountHeaderRows"/>) and its data rows; <paramref name="limit"/> caps data rows only.
        /// </summary>
        private static WireObject? Body(ViewSchedule schedule, TableData table, List<string?> headings, bool showHeaders, int limit)
        {
            var section = SectionData(table, SectionType.Body);
            if (section == null)
            {
                return null;
            }

            var rowCount = section.NumberOfRows;
            var leading = new List<List<string?>>();
            for (var i = 0; i < Math.Min(rowCount, ScheduleHeader.RowsNeeded); i++)
            {
                leading.Add(Row(schedule, section, SectionType.Body, i));
            }

            var detected = ScheduleHeader.CountHeaderRows(leading, headings, showHeaders);
            var headerRows = detected ?? 0;
            var dataRows = rowCount - headerRows;
            var rows = new List<object?>();
            for (var i = headerRows; i < rowCount && rows.Count < limit; i++)
            {
                rows.Add(i < leading.Count ? leading[i] : Row(schedule, section, SectionType.Body, i));
            }

            var body = new WireObject
            {
                { "columns", section.NumberOfColumns },
                { "header_rows", headerRows },
                { "header_cells", leading.Take(headerRows).Cast<object?>().ToList() },
                { "data_rows", dataRows },
                { "returned_rows", rows.Count },
                { "truncated", rows.Count < dataRows },
                { "rows", rows },
            };
            if (detected == null)
            {
                body["header_note"] = "Revit shows column headings, but none of the first " + ScheduleHeader.SearchRows +
                    " body rows matches the visible field headings, so no row was treated as a header: rows and data_rows may include heading rows.";
            }

            return body;
        }

        private static TableSectionData? SectionData(TableData table, SectionType type)
        {
            try
            {
                return table.GetSectionData(type);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Cell texts of the section's <paramref name="index"/>-th row (0-based from FirstRowNumber), as Revit displays them.</summary>
        private static List<string?> Row(ViewSchedule schedule, TableSectionData section, SectionType type, int index)
        {
            var colCount = section.NumberOfColumns;
            var firstCol = section.FirstColumnNumber;
            var r = section.FirstRowNumber + index;
            var cells = new List<string?>(colCount);
            for (var c = firstCol; c < firstCol + colCount; c++)
            {
                string text;
                try
                {
                    text = schedule.GetCellText(type, r, c);
                }
                catch (Exception)
                {
                    text = string.Empty;
                }

                cells.Add(text);
            }

            return cells;
        }
    }
}
