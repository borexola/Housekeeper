using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Renders a validated automation as YAML for the confirmation screen. Home Assistant is sent the JSON;
/// this exists purely so the user reads the thing they are approving in the form they know.
/// </summary>
public static class Yaml
{
    private static readonly string[] Reserved = ["true", "false", "null", "yes", "no", "on", "off", "~"];

    public static string FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        WriteObject(builder, document.RootElement, 0, inlineFirstKey: false);
        return builder.ToString().TrimEnd('\n');
    }

    private static void WriteObject(StringBuilder builder, JsonElement element, int indent, bool inlineFirstKey)
    {
        var first = true;

        foreach (var property in element.EnumerateObject())
        {
            if (!first || !inlineFirstKey) builder.Append(Pad(indent));
            first = false;

            builder.Append(Scalar(property.Name)).Append(':');
            WriteChild(builder, property.Value, indent);
        }

        if (!first) return;

        if (!inlineFirstKey) builder.Append(Pad(indent));
        builder.Append("{}\n");
    }

    private static void WriteChild(StringBuilder builder, JsonElement value, int indent)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object when value.EnumerateObject().Any():
                builder.Append('\n');
                WriteObject(builder, value, indent + 1, inlineFirstKey: false);
                break;

            case JsonValueKind.Object:
                builder.Append(" {}\n");
                break;

            case JsonValueKind.Array when value.GetArrayLength() > 0:
                builder.Append('\n');
                WriteArray(builder, value, indent + 1);
                break;

            case JsonValueKind.Array:
                builder.Append(" []\n");
                break;

            default:
                builder.Append(' ').Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static void WriteArray(StringBuilder builder, JsonElement element, int indent)
    {
        foreach (var item in element.EnumerateArray())
        {
            builder.Append(Pad(indent)).Append("- ");

            switch (item.ValueKind)
            {
                case JsonValueKind.Object when item.EnumerateObject().Any():
                    WriteObject(builder, item, indent + 1, inlineFirstKey: true);
                    break;

                case JsonValueKind.Object:
                    builder.Append("{}\n");
                    break;

                case JsonValueKind.Array when item.GetArrayLength() > 0:
                    builder.Append('\n');
                    WriteArray(builder, item, indent + 1);
                    break;

                case JsonValueKind.Array:
                    builder.Append("[]\n");
                    break;

                default:
                    builder.Append(Scalar(item)).Append('\n');
                    break;
            }
        }
    }

    private static string Pad(int indent) => new(' ', indent * 2);

    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => Scalar(element.GetString() ?? ""),
    };

    private static string Scalar(string value) =>
        NeedsQuoting(value) ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'" : value;

    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0) return true;
        if (value != value.Trim()) return true;
        if (Reserved.Contains(value, StringComparer.OrdinalIgnoreCase)) return true;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal)) return true;
        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal)) return true;

        return value.Any(ch => ch is '\n' or '\r' or '\t');
    }
}
