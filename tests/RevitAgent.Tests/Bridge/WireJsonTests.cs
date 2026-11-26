using System.IO;
using System.Text.Json;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Bridge.Wire;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// The Bridge's dependency-free JSON reader/writer (linked into the add-in) must interoperate with the
/// Host's System.Text.Json in both directions.
/// </summary>
public class WireJsonTests
{
    [Fact]
    public void RoundTrip_PreservesTypesOrderAndNesting()
    {
        var obj = new WireObject
        {
            { "id", 42L },
            { "name", "Level 1" },
            { "elev", 3.5 },
            { "neg", -7L },
            { "big", 9_007_199_254_740_993L },
            { "ok", true },
            { "none", null },
            { "list", new List<object?> { 1L, "two", false, null, new WireObject { { "x", 1.25 } } } },
            { "empty_obj", new WireObject() },
            { "empty_list", new List<object?>() },
        };

        var json = WireJson.Serialize(obj);
        Assert.Equal(
            "{\"id\":42,\"name\":\"Level 1\",\"elev\":3.5,\"neg\":-7,\"big\":9007199254740993,\"ok\":true,\"none\":null," +
            "\"list\":[1,\"two\",false,null,{\"x\":1.25}],\"empty_obj\":{},\"empty_list\":[]}",
            json);

        var back = WireJson.ParseObject(json);
        Assert.Equal(new[] { "id", "name", "elev", "neg", "big", "ok", "none", "list", "empty_obj", "empty_list" }, back.Keys.ToArray());
        Assert.Equal(42L, back["id"]);
        Assert.Equal(3.5, back["elev"]);
        Assert.Equal(9_007_199_254_740_993L, back["big"]);
        Assert.Equal(true, back["ok"]);
        Assert.Null(back["none"]);
        var list = Assert.IsType<List<object?>>(back["list"]);
        Assert.Equal(5, list.Count);
        Assert.Equal(1.25, Assert.IsType<WireObject>(list[4])["x"]);
        Assert.Equal(json, WireJson.Serialize(back));
    }

    [Theory]
    [InlineData("quote\"back\\slash/")]
    [InlineData("tabs\tnew\nlines\r\n")]
    [InlineData("ctrl\u0001\u001f\b\f")]
    [InlineData("Tiếng Việt – 日本語 – emoji 😀")]
    [InlineData("line\u2028sep\u2029")]
    public void Strings_RoundTripAndAreReadableBySystemTextJson(string text)
    {
        var json = WireJson.Serialize(new WireObject { { "s", text } });
        Assert.Equal(text, WireJson.ParseObject(json)["s"]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(text, doc.RootElement.GetProperty("s").GetString());
    }

    [Fact]
    public void Parser_ReadsSystemTextJsonOutput_IncludingEscapesAndExponents()
    {
        var stj = JsonSerializer.Serialize(new
        {
            id = "abc",
            tool = "model_query_elements",
            args = new { category = "OST_Levels", fields = new[] { "Name", "Elevation" }, limit = 5, ratio = 1e-7, path = @"C:\runs\a ""b"".rvt" },
            timeout_sec = 56,
        });
        var o = WireJson.ParseObject(stj);
        Assert.Equal("abc", o["id"]);
        var args = Assert.IsType<WireObject>(o["args"]);
        Assert.Equal("OST_Levels", args["category"]);
        Assert.Equal(5L, args["limit"]);
        Assert.Equal(1e-7, (double)args["ratio"]!, 12);
        Assert.Equal(@"C:\runs\a ""b"".rvt", args["path"]);
        Assert.Equal(new object?[] { "Name", "Elevation" }, Assert.IsType<List<object?>>(args["fields"]).ToArray());
        Assert.Equal(56L, o["timeout_sec"]);
    }

    [Fact]
    public void Parser_HandlesUnicodeEscapesAndSurrogatePairs()
    {
        var o = WireJson.ParseObject("{\"a\":\"\\u0041\\u00e9\\ud83d\\ude00\",\"b\":\"\\/\"}");
        Assert.Equal("Aé😀", o["a"]);
        Assert.Equal("/", o["b"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1 2]")]
    [InlineData("{\"a\":01}")]
    [InlineData("{\"a\":1.}")]
    [InlineData("{\"a\":\"unterminated}")]
    [InlineData("{\"a\":tru}")]
    [InlineData("{} extra")]
    [InlineData("{\"a\":\"bad \\x escape\"}")]
    [InlineData("{\"a\":\"raw\u0001control\"}")]
    public void Parser_RejectsMalformedJson(string json) =>
        Assert.ThrowsAny<FormatException>(() => WireJson.Parse(json));

    [Fact]
    public void Parser_EnforcesDepthLimit()
    {
        var deep = new string('[', WireJson.MaxDepth + 5) + new string(']', WireJson.MaxDepth + 5);
        Assert.ThrowsAny<FormatException>(() => WireJson.Parse(deep));
        var ok = new string('[', 20) + new string(']', 20);
        Assert.NotNull(WireJson.Parse(ok));
    }

    [Fact]
    public void Writer_HandlesBclTypes_AndRawFragments()
    {
        var when = new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.FromHours(7));
        var json = WireJson.Serialize(new WireObject
        {
            { "enum", DayOfWeek.Friday },
            { "when", when },
            { "guid", Guid.Parse("2b0c5ac5-295c-415b-baad-f731a61c66d8") },
            { "nan", double.NaN },
            { "inf", double.PositiveInfinity },
            { "int", 7 },
            { "short", (short)3 },
            { "dec", 1.5m },
            { "dict", new Dictionary<string, int> { ["a"] = 1 } },
            { "arr", new[] { 1, 2 } },
            { "raw", new WireRawJson("{\"pre\":[1,2]}") },
        });

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("Friday", r.GetProperty("enum").GetString());
        Assert.Equal(when, r.GetProperty("when").GetDateTimeOffset());
        Assert.Equal("2b0c5ac5-295c-415b-baad-f731a61c66d8", r.GetProperty("guid").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("nan").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("inf").ValueKind);
        Assert.Equal(7, r.GetProperty("int").GetInt32());
        Assert.Equal(3, r.GetProperty("short").GetInt32());
        Assert.Equal(1.5m, r.GetProperty("dec").GetDecimal());
        Assert.Equal(1, r.GetProperty("dict").GetProperty("a").GetInt32());
        Assert.Equal(2, r.GetProperty("arr").GetArrayLength());
        Assert.Equal(2, r.GetProperty("raw").GetProperty("pre").GetArrayLength());
    }

    [Fact]
    public void Doubles_RoundTripExactly()
    {
        foreach (var d in new[] { 0.1, 1.0 / 3.0, 9.84251968503937, -1234567.891, 1e300, 5e-324, 0.0 })
        {
            var json = WireJson.Serialize(new WireObject { { "d", d } });
            Assert.Equal(d, JsonDocument.Parse(json).RootElement.GetProperty("d").GetDouble());
            var parsed = WireJson.ParseObject(json)["d"];
            Assert.Equal(d, Convert.ToDouble(parsed));
        }
    }

    [Fact]
    public void WireObject_SetReplacesAndRemoveReindexes()
    {
        var o = new WireObject { { "a", 1L }, { "b", 2L }, { "c", 3L } };
        o["b"] = 20L;
        Assert.Equal(new[] { "a", "b", "c" }, o.Keys.ToArray());
        Assert.True(o.Remove("a"));
        Assert.False(o.Remove("zz"));
        Assert.Equal(20L, o["b"]);
        Assert.Equal(3L, o["c"]);
        Assert.Equal("{\"b\":20,\"c\":3}", o.ToString());
    }

    [Fact]
    public void WireValues_ConvertLeniently()
    {
        Assert.Equal(5L, WireValues.AsLong("5"));
        Assert.Equal(5L, WireValues.AsLong(5.0));
        Assert.Null(WireValues.AsLong(null));
        Assert.Throws<FormatException>(() => WireValues.AsLong(5.5));
        Assert.Throws<FormatException>(() => WireValues.AsLong("x"));
        Assert.True(WireValues.AsBool("true"));
        Assert.Equal(2.5, WireValues.AsDouble("2.5"));
        Assert.Equal("12", WireValues.AsString(12L));
    }

    [Fact]
    public async Task Framing_IsLittleEndianLengthPrefixedUtf8_AndInteropsWithContractsFramer()
    {
        using var ms = new MemoryStream();
        await WireFraming.WriteFrameAsync(ms, "{\"x\":\"é\"}", CancellationToken.None);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 10, 0, 0, 0 }, bytes[..4]); // "é" is 2 bytes in UTF-8
        ms.Position = 0;
        Assert.Equal("{\"x\":\"é\"}", await RevitAgent.Contracts.Framing.LengthPrefixedPipeFramer.ReadMessageAsync(ms));

        using var ms2 = new MemoryStream();
        await RevitAgent.Contracts.Framing.LengthPrefixedPipeFramer.WriteMessageAsync(ms2, "{\"y\":1}");
        ms2.Position = 0;
        Assert.Equal("{\"y\":1}", await WireFraming.ReadFrameAsync(ms2, CancellationToken.None));
        Assert.Null(await WireFraming.ReadFrameAsync(ms2, CancellationToken.None)); // clean EOF
    }

    [Fact]
    public async Task Framing_RejectsOversizedFrames()
    {
        using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        await Assert.ThrowsAsync<InvalidDataException>(() => WireFraming.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public void PipeNames_RoundTripPid()
    {
        Assert.Equal("revitagent-bridge-1234", BridgePipeNames.ForProcess(1234));
        Assert.Equal(1234, BridgePipeNames.TryParsePid("revitagent-bridge-1234"));
        Assert.Null(BridgePipeNames.TryParsePid("revitagent-bridge-x"));
        Assert.Null(BridgePipeNames.TryParsePid("other-1234"));
    }
}
