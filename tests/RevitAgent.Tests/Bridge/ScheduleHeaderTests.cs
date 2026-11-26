using RevitAgent.Contracts.Bridge;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// schedule_read splits the schedule body into header rows and data rows (live finding 2026-11-26: a Level schedule of
/// 33 levels reported 35 rows because the body section includes the heading row and the blank row under it).
/// </summary>
public class ScheduleHeaderTests
{
    private static readonly string[] Headings = { "Level", "Number", "Name" };

    private static int? Count(bool showHeaders, string[] headings, params string[][] rows) =>
        ScheduleHeader.CountHeaderRows(rows, headings, showHeaders);

    [Fact]
    public void HeadingRow_ThenBlankSpacer_AreBothHeader()
    {
        // As read live on Revit 2024 (docs/evidence/demo-2026-11-25, "AI Demo - Rooms by Level").
        Assert.Equal(2, Count(true, Headings,
            new[] { "Level", "Number", "Name" },
            new[] { "", "", "" },
            new[] { "Parking", "E1", "Elevator" },
            new[] { "Parking", "P01", "Parking Garage" }));
    }

    [Fact]
    public void HeadingRow_WithoutSpacer_IsTheOnlyHeaderRow()
    {
        Assert.Equal(1, Count(true, new[] { "Name", "Elevation" },
            new[] { "Name", "Elevation" },
            new[] { "Level 1", "0" },
            new[] { "Level 2", "3000" }));
    }

    [Fact]
    public void GroupedHeaders_CaptionRowAboveTheHeadings_IsHeader()
    {
        // "Identity" groups Level + Number; the caption is reported in every merged cell, Name spans both rows.
        Assert.Equal(3, Count(true, Headings,
            new[] { "Identity", "Identity", "Name" },
            new[] { "Level", "Number", "Name" },
            new[] { "", "", "" },
            new[] { "L1", "101", "Office" }));
    }

    [Fact]
    public void GroupedHeaders_TextOnlyInTheFirstCellOfAMerge_StillFound()
    {
        // Caption only in the merge's first cell; the vertically merged "Name" only in the top row.
        Assert.Equal(3, Count(true, Headings,
            new[] { "Identity", "", "Name" },
            new[] { "Level", "Number", "" },
            new[] { "", "", "" },
            new[] { "L1", "101", "Office" }));
    }

    [Fact]
    public void NestedGroupedHeaders_WithoutSpacer()
    {
        Assert.Equal(3, Count(true, Headings,
            new[] { "Room data", "Room data", "Room data" },
            new[] { "Identity", "Identity", "Name" },
            new[] { "Level", "Number", "Name" },
            new[] { "L1", "101", "Office" }));
    }

    [Fact]
    public void HeadersHidden_NoRowIsHeader_EvenALeadingEmptyOrHeadingLikeRow()
    {
        Assert.Equal(0, Count(false, Headings, new[] { "L1", "101", "Office" }, new[] { "L1", "102", "Hall" }));
        Assert.Equal(0, Count(false, Headings, new[] { "", "", "" }, new[] { "L1", "102", "Hall" }));
        Assert.Equal(0, Count(false, Headings, new[] { "Level", "Number", "Name" }, new[] { "L1", "102", "Hall" }));
    }

    [Fact]
    public void EmptyDataRow_AfterTheSpacer_StaysData()
    {
        // Only one blank row under the headings is the spacer; the next one is a data row whose cells are empty.
        Assert.Equal(2, Count(true, Headings,
            new[] { "Level", "Number", "Name" },
            new[] { "", "", "" },
            new[] { "", "", "" },
            new[] { "L1", "101", "Office" }));
    }

    [Fact]
    public void EmptyDataRow_InTheMiddle_StaysData()
    {
        Assert.Equal(2, Count(true, Headings,
            new[] { "Level", "Number", "Name" },
            new[] { "", "", "" },
            new[] { "L1", "101", "Office" },
            new[] { "", "", "" },
            new[] { "L2", "201", "Hall" }));
    }

    [Fact]
    public void DataRowEqualToTheHeadings_LaterInTheBody_StaysData()
    {
        Assert.Equal(2, Count(true, Headings,
            new[] { "Level", "Number", "Name" },
            new[] { "", "", "" },
            new[] { "Level", "Number", "Name" }));
    }

    [Fact]
    public void Headings_MatchIgnoringCaseAndSurroundingSpace()
    {
        Assert.Equal(2, Count(true, Headings, new[] { " LEVEL", "number ", "Name" }, new[] { " ", "", "" }, new[] { "L1", "101", "Office" }));
    }

    [Fact]
    public void OnlyHeadingsAndSpacer_NoData()
    {
        Assert.Equal(2, Count(true, Headings, new[] { "Level", "Number", "Name" }, new[] { "", "", "" }));
        Assert.Equal(1, Count(true, Headings, new[] { "Level", "Number", "Name" }));
    }

    [Fact]
    public void HeadingsShownButNotFound_IsNull()
    {
        Assert.Null(Count(true, Headings, new[] { "L1", "101", "Office" }, new[] { "L1", "102", "Hall" }));
        // A partial match (one heading differs) is not a heading row.
        Assert.Null(Count(true, Headings, new[] { "Level", "No.", "Name" }, new[] { "L1", "102", "Hall" }));
    }

    [Fact]
    public void HeadingRow_BeyondTheSearchWindow_IsNotFound()
    {
        var rows = new List<string[]>();
        for (var i = 0; i < ScheduleHeader.SearchRows; i++)
        {
            rows.Add(new[] { "Group " + i, "Group " + i, "Group " + i });
        }

        rows.Add(new[] { "Level", "Number", "Name" });
        Assert.Null(Count(true, Headings, rows.ToArray()));
        rows.RemoveAt(0);
        Assert.Equal(ScheduleHeader.SearchRows, Count(true, Headings, rows.ToArray()));
    }

    [Fact]
    public void EmptyBodyOrNoVisibleFields_HasNoHeaderRows()
    {
        Assert.Equal(0, Count(true, Headings));
        Assert.Equal(0, Count(true, Array.Empty<string>(), new[] { "x" }));
    }

    [Fact]
    public void AllHeadingsEmpty_TheEmptyHeadingRowAndSpacer_AreHeader()
    {
        Assert.Equal(2, Count(true, new[] { "", "" }, new[] { "", "" }, new[] { "", "" }, new[] { "a", "b" }));
    }

    [Fact]
    public void IsBlank_TreatsWhitespaceAndNullAsEmpty()
    {
        Assert.True(ScheduleHeader.IsBlank(new string?[] { null, " ", "" }));
        Assert.False(ScheduleHeader.IsBlank(new[] { "", "0" }));
    }
}
