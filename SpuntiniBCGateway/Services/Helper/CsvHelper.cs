// Simple CSV parser that handles quoted fields and commas inside quotes.
using System.Text;

namespace SpuntiniBCGateway.Services;

public class CsvHelper
{
    internal static string[] ParseCsvLine(string line, string csvDelimiter = ",")
    {
        if (line == null)
            return Array.Empty<string>();

        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        char delim = (csvDelimiter ?? ",")[0];

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                // Opening quote only allowed at start of field
                if (!inQuotes && sb.Length == 0)
                {
                    inQuotes = true;
                    continue;
                }

                if (inQuotes)
                {
                    // Handle escaped quote sequence "" -> append single quote
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip escaped quote
                        continue;
                    }

                    // If this quote is immediately before a delimiter or end-of-line, treat as closing quote
                    if (i + 1 == line.Length || line[i + 1] == delim)
                    {
                        inQuotes = false;
                        continue;
                    }

                    // Otherwise it's an interior quote inside a quoted field: treat as literal
                    sb.Append('"');
                    continue;
                }

                // Quote in unquoted field -> literal
                sb.Append('"');
            }
            else if (c == delim && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result.ToArray();
    }

    internal static Dictionary<string, string> GetValueMap(Encoding encoding, string line, string[] headers, string csvDelimiter = ",")
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(line) || headers is null || headers.Length <= 0) return map;
        encoding ??= Encoding.UTF8;

        if (encoding != Encoding.UTF8)
            line = Encoding.UTF8.GetString(Encoding.Convert(encoding, Encoding.UTF8, encoding.GetBytes(line)));

        string[] fields = ParseCsvLine(line, csvDelimiter);
        for (int i = 0; i < headers.Length && i < fields.Length; i++)
        {
            string key = headers[i]?.Trim() ?? string.Empty;
            string val = fields[i]?.Trim() ?? string.Empty;
            if (val.Length >= 2 && val.StartsWith('\"') && val.EndsWith('\"'))
                val = val[1..^1];
            map[key] = val;
        }

        return map;
    }
}