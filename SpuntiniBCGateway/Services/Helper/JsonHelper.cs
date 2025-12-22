// Simple CSV parser that handles quoted fields and commas inside quotes.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpuntiniBCGateway.Services;

public class JsonHelper
{
    internal static async Task<string> RemoveFieldsFromJsonAsync(string jsonString, string[]? fieldsToExclude = null, EventLog? logger = null, string company = "")
    {
        if (string.IsNullOrWhiteSpace(jsonString) || fieldsToExclude == null || fieldsToExclude.Length == 0)
            return jsonString;

        try
        {
            // Deserialize into a dictionary of JsonElement so we preserve types
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonString);
            if (map != null)
            {
                var filtered = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in map)
                {
                    bool skip = false;
                    foreach (string ex in fieldsToExclude)
                    {
                        if (string.Equals(kvp.Key, ex, StringComparison.OrdinalIgnoreCase))
                        {
                            skip = true;
                            break;
                        }
                    }
                    if (!skip)
                        filtered[kvp.Key] = kvp.Value;
                }

                jsonString = JsonSerializer.Serialize(filtered);
            }
        }
        catch (Exception ex)
        {
            // If anything goes wrong while filtering, fall back to original body but log warning
            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Could not filter fields before PATCH: {ex.Message}");
        }

        return jsonString;
    }

    internal static async Task<bool> IsPatchRequiredAsync(Dictionary<string, string>? existingData, string? jsonString, string[]? fieldsToExclude = null,
        Dictionary<string, List<string>>? fieldExistingValuesToExclude = null, EventLog? logger = null, string company = "")
    {
        if (string.IsNullOrWhiteSpace(jsonString) || existingData == null || existingData.Count == 0)
            return false;

        try
        {
            // Deserialize into a dictionary of JsonElement so we preserve types
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonString);
            if (map == null) return false;

            foreach (var kvp in map)
            {
                if (fieldsToExclude != null && fieldsToExclude.Contains(kvp.Key)) continue;

                List<string>? valuesToExclude = [];

                fieldExistingValuesToExclude?.TryGetValue(kvp.Key, out valuesToExclude);

                if (existingData.TryGetValue(kvp.Key, out string? existingValue))
                {
                    // Skip if value must remain
                    if (valuesToExclude is not null && valuesToExclude.Contains(existingValue)) continue;
                    string newValue = kvp.Value.ToString() ?? "";

                    if (!string.Equals(existingValue, newValue, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            // If anything goes wrong while filtering, fall back to original body but log warning
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
        }

        return false;
    }


    public static List<JsonElement> GetItemsSafe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<JsonElement>();
        foreach (var item in doc.RootElement.EnumerateArray())
            list.Add(item.Clone()); // ✅ clone each element
        return list; // safe to return
    }


    /// <summary>
    /// Past de tekstwaarde aan van een parameter in een JSON-string met eenvoudige dot-notatie (geen array indices).
    /// Gooit een exception als de target geen string is of niet bestaat (tenzij allowCreate = true).
    /// </summary>
    public static string ReplacePathValues(string json, Dictionary<string, object> pathValueDictionary, bool allowCreate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(pathValueDictionary);

        if (!pathValueDictionary.Any()) return json;

        var options = new JsonSerializerOptions
        {
            // Zorg dat we case consistent houden; pas desgewenst aan.
            WriteIndented = false
        };

        var root = JsonNode.Parse(json) ?? throw new ArgumentException("Invalid json");

        foreach (var path in pathValueDictionary.Keys)
        {
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonNode? current = root;

            for (int i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];

                // Geen array-ondersteuning in deze eenvoudige helper
                if (seg.Contains('['))
                    throw new NotSupportedException("Array indices worden niet ondersteund in deze methode.");

                if (current is not JsonObject obj)
                    throw new InvalidOperationException($"Segment '{seg}' valt niet onder een object.");

                bool last = i == segments.Length - 1;

                string? newTextValue = pathValueDictionary[path].ToString();
                newTextValue ??= "";

                if (!obj.TryGetPropertyValue(seg, out var next))
                {
                    if (!allowCreate)
                        throw new ArgumentException($"Pad '{path}' bestaat niet (ontbreekt segment '{seg}').");

                    if (last)
                    {
                        obj[seg] = newTextValue; // string toevoegen
                        break;
                    }
                    else
                    {
                        var created = new JsonObject();
                        obj[seg] = created;
                        current = created;
                        continue;
                    }
                }

                if (last)
                {
                    // Alleen stringwaarden aanpassen
                    if (next is JsonValue jv && jv.TryGetValue(out string? existingStr))
                    {
                        obj[seg] = newTextValue;
                        break;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Pad '{path}' verwijst niet naar een string (gevonden: {next?.GetType().Name ?? "null"}).");
                    }
                }

                current = next;
            }
        }

        return root.ToJsonString(options);
    }

}