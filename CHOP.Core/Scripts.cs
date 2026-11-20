using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chop.Core;

/// <summary>Runs JSON command arrays with optional whole-value parameter references.</summary>
public static class Scripts
{
    public static void Run(CommandEngine engine, Stream input, IReadOnlyDictionary<string, string> parameters)
    {
        if (JsonNode.Parse(input) is not JsonArray commands) throw new InvalidDataException("Expected a JSON command array.");
        for (int i = 0; i < commands.Count; i++)
        {
            try
            {
                var command = Expand(commands[i], parameters).Deserialize<Command>(Format.Json)
                    ?? throw new InvalidDataException("Expected a command object.");
                engine.Execute(command);
            }
            catch (Exception error)
            {
                throw new InvalidDataException($"Command {i + 1}: {error.Message}", error);
            }
        }
    }

    /// <summary>Turns recorded output paths into required arguments to prevent replay from overwriting the originals.</summary>
    public static JsonArray FromRecording(IEnumerable<LogEntry> entries, bool includeOutputs)
    {
        var script = new JsonArray();
        int output = 0;
        foreach (var entry in entries)
        {
            if (!entry.Success) continue;
            bool writesFile = entry.Command.Name is "document.save" or "document.export";
            if (writesFile && !includeOutputs) continue;
            var node = JsonSerializer.SerializeToNode(entry.Command, Format.Json)!.AsObject();
            if (writesFile) node["args"]!.AsObject()["path"] = new JsonObject { ["$param"] = $"output{++output}" };
            script.Add(node);
        }
        return script;
    }

    private static JsonNode? Expand(JsonNode? node, IReadOnlyDictionary<string, string> parameters)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj["$param"] is JsonValue value && value.TryGetValue<string>(out var name))
                return parameters.TryGetValue(name, out var json) ? JsonNode.Parse(json)
                    : throw new InvalidDataException($"Missing script parameter '{name}'.");
            return new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Expand(pair.Value, parameters))));
        }
        if (node is JsonArray array) return new JsonArray(array.Select(item => Expand(item, parameters)).ToArray());
        return node?.DeepClone();
    }
}
