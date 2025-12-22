namespace SpuntiniBCGateway.Services;

public class LanguageSanitizer
{
    const string _defaultLanguageCode = "NLB";

    public static string GetBcLangageCode(string? language, string defaultLanguageCode = _defaultLanguageCode, string systemFrench = "FRB")
    {
        if (string.IsNullOrEmpty(language)) return defaultLanguageCode;

        if (language.StartsWith("NL", StringComparison.InvariantCultureIgnoreCase)) return "NLB";
        if (language.StartsWith("FR", StringComparison.InvariantCultureIgnoreCase)) return systemFrench;

        return defaultLanguageCode;
    }
}