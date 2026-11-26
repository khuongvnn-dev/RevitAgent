using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAgent.Core.Tools;

/// <summary>
/// Small fluent builder for tool input JSON Schemas.
/// <code>
/// Schema.Object()
///     .Integer("pid", "Revit process id (from revit_list_instances).", required: true)
///     .String("selector", "UI selector, e.g. id=btnOk or name=\"OK\" type=Button")
///     .Boolean("include_images", "Attach a PNG.", defaultValue: false)
/// </code>
/// </summary>
public sealed class Schema
{
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = new();
    private readonly string? _description;

    private Schema(string? description) => _description = description;

    public static Schema Object(string? description = null) => new(description);

    public Schema String(string name, string description, bool required = false, IEnumerable<string>? enumValues = null, string? defaultValue = null)
    {
        var p = new JsonObject { ["type"] = "string", ["description"] = description };
        if (enumValues is not null)
        {
            p["enum"] = new JsonArray(enumValues.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        }

        if (defaultValue is not null)
        {
            p["default"] = defaultValue;
        }

        return Add(name, p, required);
    }

    public Schema Integer(string name, string description, bool required = false, long? defaultValue = null, long? minimum = null, long? maximum = null)
    {
        var p = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (defaultValue is not null)
        {
            p["default"] = defaultValue;
        }

        if (minimum is not null)
        {
            p["minimum"] = minimum;
        }

        if (maximum is not null)
        {
            p["maximum"] = maximum;
        }

        return Add(name, p, required);
    }

    public Schema Number(string name, string description, bool required = false, double? defaultValue = null)
    {
        var p = new JsonObject { ["type"] = "number", ["description"] = description };
        if (defaultValue is not null)
        {
            p["default"] = defaultValue;
        }

        return Add(name, p, required);
    }

    public Schema Boolean(string name, string description, bool required = false, bool? defaultValue = null)
    {
        var p = new JsonObject { ["type"] = "boolean", ["description"] = description };
        if (defaultValue is not null)
        {
            p["default"] = defaultValue;
        }

        return Add(name, p, required);
    }

    public Schema StringArray(string name, string description, bool required = false)
    {
        var p = new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = new JsonObject { ["type"] = "string" } };
        return Add(name, p, required);
    }

    /// <summary>Free-form JSON object argument (e.g. arguments forwarded to another tool).</summary>
    public Schema AnyObject(string name, string description, bool required = false)
    {
        var p = new JsonObject { ["type"] = "object", ["description"] = description, ["additionalProperties"] = true };
        return Add(name, p, required);
    }

    /// <summary>Escape hatch: add a raw JSON-schema property.</summary>
    public Schema Raw(string name, JsonObject propertySchema, bool required = false) => Add(name, propertySchema, required);

    /// <summary>
    /// Standard <c>pid</c> argument used by every tool that targets one Revit process. When optional, the tool uses
    /// the only running Revit and asks for pid when several run.
    /// </summary>
    public Schema Pid(bool required = true) =>
        Integer("pid", required ? "Revit pid (revit_list_instances)." : "Revit pid (revit_list_instances); optional when one Revit runs.", required);

    public JsonElement Build()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = _properties.DeepClone(),
            ["additionalProperties"] = false,
        };
        if (_description is not null)
        {
            root["description"] = _description;
        }

        if (_required.Count > 0)
        {
            root["required"] = _required.DeepClone();
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private Schema Add(string name, JsonObject p, bool required)
    {
        _properties[name] = p;
        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
