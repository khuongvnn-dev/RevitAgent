namespace RevitAgent.Bridge.Tools
{
    using System;
    using System.Globalization;
    using Autodesk.Revit.DB;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// Reads parameter values in a uniform shape: <c>value</c> = raw API value (doubles in Revit internal
    /// units, e.g. feet), <c>display</c> = Revit's formatted string in project units, and for measurable
    /// doubles <c>display_number</c> = the value converted to the project's display unit.
    /// </summary>
    internal static class ParameterReader
    {
        public static string StorageName(StorageType st)
        {
            switch (st)
            {
                case StorageType.String: return "string";
                case StorageType.Integer: return "integer";
                case StorageType.Double: return "double";
                case StorageType.ElementId: return "element_id";
                default: return "none";
            }
        }

        public static object? RawValue(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsInteger();
                case StorageType.Double: return p.AsDouble();
                case StorageType.ElementId: return Ids.OrNull(p.AsElementId());
                default: return null;
            }
        }

        public static string? Display(Parameter p)
        {
            try
            {
                return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static ForgeTypeId? DisplayUnit(Parameter p, Document doc)
        {
            if (p.StorageType != StorageType.Double)
            {
                return null;
            }

            try
            {
                var spec = p.Definition?.GetDataType();
                if (spec == null || spec.Empty() || !UnitUtils.IsMeasurableSpec(spec))
                {
                    return null;
                }

                return doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Number for comparisons: doubles in display units when measurable, integers, ids, numeric strings.</summary>
        public static double? Number(Parameter p, Document doc)
        {
            switch (p.StorageType)
            {
                case StorageType.Double:
                    var v = p.AsDouble();
                    var unit = DisplayUnit(p, doc);
                    return unit != null ? UnitUtils.ConvertFromInternalUnits(v, unit) : v;
                case StorageType.Integer:
                    return p.AsInteger();
                case StorageType.ElementId:
                    return Ids.Of(p.AsElementId());
                case StorageType.String:
                    return double.TryParse(p.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                default:
                    return null;
            }
        }

        /// <summary>Compact field value used by model_query_elements.</summary>
        public static WireObject Field(Parameter p, Document doc)
        {
            var o = new WireObject
            {
                { "value", RawValue(p) },
                { "display", Display(p) },
                { "storage", StorageName(p.StorageType) },
            };
            AddUnit(o, p, doc);
            return o;
        }

        /// <summary>Full description used by model_get_element.</summary>
        public static WireObject Full(Parameter p, Document doc)
        {
            var def = p.Definition;
            var o = new WireObject
            {
                { "name", def?.Name },
                { "storage", StorageName(p.StorageType) },
                { "value", RawValue(p) },
                { "display", Display(p) },
                { "has_value", p.HasValue },
                { "read_only", p.IsReadOnly },
            };
            AddUnit(o, p, doc);
            if (def is InternalDefinition internalDef && internalDef.BuiltInParameter != BuiltInParameter.INVALID)
            {
                o["builtin"] = internalDef.BuiltInParameter.ToString();
            }

            if (p.IsShared)
            {
                o["shared_guid"] = p.GUID.ToString();
            }

            try
            {
                var dataType = def?.GetDataType();
                if (dataType != null && !dataType.Empty())
                {
                    o["data_type"] = dataType.TypeId;
                }
            }
            catch (Exception)
            {
            }

            var group = GroupLabel(def);
            if (group != null)
            {
                o["group"] = group;
            }

            if (p.StorageType == StorageType.ElementId)
            {
                var target = p.AsElementId();
                if (target != null && target != ElementId.InvalidElementId)
                {
                    var e = doc.GetElement(target);
                    if (e != null)
                    {
                        o["element_name"] = Describe.SafeName(e);
                    }
                }
            }

            return o;
        }

        private static void AddUnit(WireObject o, Parameter p, Document doc)
        {
            var unit = DisplayUnit(p, doc);
            if (unit == null)
            {
                return;
            }

            try
            {
                o["display_number"] = Describe.Round(UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit));
                o["unit"] = LabelUtils.GetLabelForUnit(unit);
            }
            catch (Exception)
            {
            }
        }

        private static string? GroupLabel(Definition? def)
        {
            if (def == null)
            {
                return null;
            }

            try
            {
#if REVIT2024_OR_GREATER
                var g = def.GetGroupTypeId();
                return g == null || g.Empty() ? null : LabelUtils.GetLabelForGroup(g);
#else
                return LabelUtils.GetLabelFor(def.ParameterGroup);
#endif
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Looks a parameter up by display name, or by BuiltInParameter name (e.g. LEVEL_ELEV).</summary>
        public static Parameter? Lookup(Element e, string name)
        {
            if (name.IndexOf('_') > 0 && name.ToUpperInvariant() == name &&
                Enum.TryParse(name, false, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
            {
                var builtIn = e.get_Parameter(bip);
                if (builtIn != null)
                {
                    return builtIn;
                }
            }

            return e.LookupParameter(name);
        }
    }
}
