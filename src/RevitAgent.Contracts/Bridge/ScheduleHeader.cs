namespace RevitAgent.Contracts.Bridge
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Finds where the column headings of a schedule body end and its data rows begin, from the body's cell texts.
    /// Revit's body section (TableSectionData of SectionType.Body) starts with the header: grouped-header caption rows
    /// (ViewSchedule.GroupHeaders), the row of column headings, and usually one blank spacer row. The Revit API has no
    /// header row count (ViewSchedule.GetScheduleHeightsOnSheet is 2022.1+ and describes sheet layout), so the Bridge anchors on
    /// what every supported year exposes: ScheduleDefinition.ShowHeaders and the visible fields' ColumnHeading texts in column
    /// order. Shared (linked source) by the Bridge and the tests.
    /// Plain C# for net48 and net8+.
    /// </summary>
    public static class ScheduleHeader
    {
        /// <summary>The heading row is looked for in this many leading rows (grouped-header caption rows sit above it).</summary>
        public const int SearchRows = 10;

        /// <summary>Leading body rows the caller passes: the searched rows plus the spacer under a heading row found in the last one.</summary>
        public const int RowsNeeded = SearchRows + 1;

        /// <summary>
        /// Number of leading rows of <paramref name="rows"/> that belong to the header.
        /// <list type="bullet">
        /// <item>0 when <paramref name="showHeaders"/> is false (Revit shows no headings) or there are no headings or rows.</item>
        /// <item>Otherwise the heading row is the first row (within the first <see cref="SearchRows"/> rows) whose cells equal the
        /// headings in order (trimmed, case-insensitive). A heading cell may be empty when the same column shows the heading in a
        /// row above (a vertically merged heading next to grouped columns). Every row above it is a grouped-header row; one fully
        /// empty row right below it is the spacer and belongs to the header too. Later rows are data, even empty ones.</item>
        /// <item>null when headings are shown but no row matched them: the caller cannot tell headings from data.</item>
        /// </list>
        /// </summary>
        /// <param name="rows">Leading body rows (at least <see cref="RowsNeeded"/> when the body has that many), cell texts in column order.</param>
        /// <param name="headings">Column headings of the visible fields, in column order.</param>
        /// <param name="showHeaders">ScheduleDefinition.ShowHeaders.</param>
        public static int? CountHeaderRows(IReadOnlyList<IReadOnlyList<string?>> rows, IReadOnlyList<string?> headings, bool showHeaders)
        {
            if (!showHeaders || rows.Count == 0 || headings.Count == 0)
            {
                return 0;
            }

            var last = Math.Min(rows.Count, SearchRows);
            for (var r = 0; r < last; r++)
            {
                if (IsHeadingRow(rows, r, headings))
                {
                    var count = r + 1;
                    if (count < rows.Count && IsBlank(rows[count]))
                    {
                        count++;
                    }

                    return count;
                }
            }

            return null;
        }

        /// <summary>True when every cell of the row is empty or whitespace.</summary>
        public static bool IsBlank(IReadOnlyList<string?> row)
        {
            foreach (var cell in row)
            {
                if (Norm(cell).Length > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsHeadingRow(IReadOnlyList<IReadOnlyList<string?>> rows, int r, IReadOnlyList<string?> headings)
        {
            var columns = Math.Min(headings.Count, rows[r].Count);
            if (columns == 0)
            {
                return false;
            }

            var matched = false;
            var allHeadingsEmpty = true;
            for (var c = 0; c < columns; c++)
            {
                var heading = Norm(headings[c]);
                var cell = Norm(rows[r][c]);
                if (heading.Length > 0)
                {
                    allHeadingsEmpty = false;
                }

                if (string.Equals(cell, heading, StringComparison.OrdinalIgnoreCase))
                {
                    matched |= heading.Length > 0;
                    continue;
                }

                if (cell.Length == 0 && HeadingAbove(rows, r, c, heading))
                {
                    continue;
                }

                return false;
            }

            // A schedule whose headings are all empty shows an empty heading row.
            return matched || allHeadingsEmpty;
        }

        private static bool HeadingAbove(IReadOnlyList<IReadOnlyList<string?>> rows, int r, int c, string heading)
        {
            for (var above = r - 1; above >= 0; above--)
            {
                if (c < rows[above].Count && string.Equals(Norm(rows[above][c]), heading, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Norm(string? s) => s == null ? string.Empty : s.Trim();
    }
}
