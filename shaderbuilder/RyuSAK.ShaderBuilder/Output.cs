using System.Collections;
using System.Text.Json.Nodes;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// Progress and results, either as readable text or as one JSON object per line for RyuSAK.
/// </summary>
static class Output
{
    private static readonly object Lock = new();
    public static bool Json;

    private static void Emit(Dictionary<string, object> payload, string text)
    {
        lock (Lock)
        {
            Console.Out.WriteLine(Json ? ToNode(payload).ToJsonString() : text);
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Builds JSON without reflection, which trimmed single-file builds don't support.
    /// </summary>
    public static JsonNode ToNode(object value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case uint u:
                return JsonValue.Create(u);
            case ulong ul:
                return JsonValue.Create(ul);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case IDictionary dictionary:
                JsonObject obj = new();
                foreach (DictionaryEntry entry in dictionary)
                {
                    obj[entry.Key.ToString()] = ToNode(entry.Value);
                }

                return obj;
            case IEnumerable list:
                JsonArray array = new();
                foreach (object item in list)
                {
                    array.Add(ToNode(item));
                }

                return array;
            default:
                return JsonValue.Create(value.ToString());
        }
    }

    public static void Progress(string phase, long done, long total, string message)
    {
        Emit(new Dictionary<string, object>
        {
            ["event"] = "progress",
            ["phase"] = phase,
            ["done"] = done,
            ["total"] = total,
            ["message"] = message,
        }, total > 0 ? $"[{phase}] {message} ({done}/{total})" : $"[{phase}] {message}");
    }

    public static void Status(string message) => Progress("status", 0, 0, message);

    public static void Result(Dictionary<string, object> result, string text)
    {
        result["event"] = "result";
        Emit(result, text);
    }

    public static void Error(string code, string message)
    {
        Emit(new Dictionary<string, object>
        {
            ["event"] = "error",
            ["code"] = code,
            ["message"] = message,
        }, $"Error ({code}): {message}");
    }
}
