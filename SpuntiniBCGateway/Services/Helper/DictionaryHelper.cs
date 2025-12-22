namespace SpuntiniBCGateway.Services;

public static class DictionaryHelper
{
    // Tries to get a value from the dictionary by key, ignoring case.
    public static bool TryGet(Dictionary<string, string> dict, string key, out string? value)
    {
        if (dict != null && dict.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
        {
            value = v;
            return true;
        }
        
        value = null;
        return false;
    }
}