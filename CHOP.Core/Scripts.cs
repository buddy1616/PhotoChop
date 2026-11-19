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
