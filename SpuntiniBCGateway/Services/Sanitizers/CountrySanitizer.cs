namespace SpuntiniBCGateway.Services;

public class CountrySanitizer
{
    const string _defaultCountryCode = "BE";

    public static string GetCountryIso2Code(string? countryText, string defaultCountryCode = _defaultCountryCode)
    {
        if (string.IsNullOrEmpty(countryText)) return defaultCountryCode;

        if (countryText.Length == 2) return countryText;

        if (countryText.Length > 2)
        {
            if (countryText.StartsWith("BE", StringComparison.InvariantCultureIgnoreCase)) return "BE";
            if (countryText.StartsWith("SP", StringComparison.InvariantCultureIgnoreCase)) return "ES";
            if (countryText.StartsWith("FR", StringComparison.InvariantCultureIgnoreCase)) return "FR";
            if (countryText.StartsWith("NE", StringComparison.InvariantCultureIgnoreCase) ||
                countryText.StartsWith("THE NE", StringComparison.InvariantCultureIgnoreCase)) return "NL";
            if (countryText.StartsWith("DU", StringComparison.InvariantCultureIgnoreCase) ||
                countryText.StartsWith("GE", StringComparison.InvariantCultureIgnoreCase)) return "DE";
            if (countryText.Equals("POL", StringComparison.InvariantCultureIgnoreCase)) return "PL";            
            if (countryText.Equals("POR", StringComparison.InvariantCultureIgnoreCase)) return "PT";            
        } 
        else if (countryText.Length == 1)
        {
            if (countryText.Equals("B", StringComparison.InvariantCultureIgnoreCase)) return "BE";   
            if (countryText.Equals("P", StringComparison.InvariantCultureIgnoreCase)) return "PT";   
        }
                        
        return defaultCountryCode;
    }
}