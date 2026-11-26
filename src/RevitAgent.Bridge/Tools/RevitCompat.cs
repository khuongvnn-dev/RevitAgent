namespace RevitAgent.Bridge.Tools
{
    using System;
    using Autodesk.Revit.DB;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// Revit API differences between 2022 and 2026 in one place.
    /// ElementId: 2024+ is 64-bit (<c>Value</c>, <c>new ElementId(long)</c>); <c>IntegerValue</c> and the
    /// int constructor are obsolete in 2024/2025 and removed in 2026. Category.BuiltInCategory exists 2023+.
    /// </summary>
    internal static class Ids
    {
        public static long Of(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId Make(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw ToolArgs.Invalid("Element id " + value + " is out of range for this Revit version.");
            }

            return new ElementId((int)value);
#endif
        }

        /// <summary>Null for null/InvalidElementId.</summary>
        public static object? OrNull(ElementId? id) => id == null || id == ElementId.InvalidElementId ? null : (object)Of(id);

        public static string? BuiltInCategoryName(ElementId categoryId)
        {
            var v = Of(categoryId);
            if (v >= 0)
            {
                return null; // user/sub categories have positive ids
            }

            try
            {
                var bic = (BuiltInCategory)Enum.ToObject(typeof(BuiltInCategory), v);
                return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Compact element/category descriptions shared by tools.</summary>
    internal static class Describe
    {
        public static string? SafeName(Element e)
        {
            try
            {
                return e.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static WireObject? Category(Category? c)
        {
            if (c == null)
            {
                return null;
            }

            return new WireObject
            {
                { "id", Ids.Of(c.Id) },
                { "name", c.Name },
                { "builtin", Ids.BuiltInCategoryName(c.Id) },
            };
        }

        public static WireObject Brief(Element e)
        {
            var o = new WireObject
            {
                { "id", Ids.Of(e.Id) },
                { "unique_id", e.UniqueId },
                { "name", SafeName(e) },
                { "category", e.Category?.Name },
                { "class", e.GetType().Name },
            };
            return o;
        }

        public static WireObject Xyz(XYZ p) => new WireObject { { "x", Round(p.X) }, { "y", Round(p.Y) }, { "z", Round(p.Z) } };

        public static WireObject View(View v) => new WireObject
        {
            { "id", Ids.Of(v.Id) },
            { "name", v.Name },
            { "view_type", v.ViewType.ToString() },
        };

        public static double Round(double d) => Math.Round(d, 6);
    }
}
