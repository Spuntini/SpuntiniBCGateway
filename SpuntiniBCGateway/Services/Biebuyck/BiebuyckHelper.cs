using System.Globalization;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public partial class BiebuyckHelper
{
    public static Dictionary<double, string> GetBcVatBusPostingGroupMapping(IConfiguration config, string company)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        ArgumentNullException.ThrowIfNull(config);
        string sectionPath = $"Companies:{company}:VatData:VatBusPostingGroupMapping";
        var vatDataSection = config.GetSection(sectionPath);

        if (!vatDataSection.Exists())
            throw new InvalidOperationException(
                $"Configuratiesectie '{sectionPath}' werd niet gevonden.");

        var dict = new Dictionary<double, string>();

        foreach (var child in vatDataSection.GetChildren())
        {
            // child.Key = sleutel in appsettings, child.Value = stringwaarde
            // Lege of null keys overslaan
            if (!string.IsNullOrWhiteSpace(child.Key))
            {
                StringHelper.TryParseDouble(child.Value, CultureInfo.CurrentCulture, out double taxRate);
                dict[taxRate] = child.Key ?? string.Empty;
            }
        }

        return dict;
    }
    
    public static string? GetBcUom(string uom, string? defaultUom = UomHelper._defaultSystemUom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return defaultUom;

        return uom.ToUpperInvariant() switch
        {
            "STUKS" or "KG" or "DOOS" or "KARTON" => uom,// valid UOM codes
            "KART" or "KT" or "1KART" => "KARTON",
            "VE" => "STUK",
            "LITER" => "L",
            _ => defaultUom,
        };
    }

    public static int? GetBcQtyPerUnitOfMeasure(string uom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return 1;

        return uom.ToUpperInvariant() switch
        {
            "STUKS" => 1,
            _ => 100,
        };
    }

     public static int? GetBcQtyRoundingPrecision(string uom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return 1;

        return uom.ToUpperInvariant() switch
        {
            "STUKS" => 1,
            _ => 0,
        };
    }
   
    public static DateTime? ParseToDateTime(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr) || dateStr == "-  -")
            return null;

        // Try parsing DD-MM-YYYY format (common in Dutch systems)
        if (DateTime.TryParseExact(dateStr, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return dt;

        // Try other common formats
        if (DateTime.TryParse(dateStr, out var dt2))
            return dt2;

        return null;
    }

    public static string? ParseDate(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr) || dateStr == "-  -")
            return null;

        // Try parsing DD-MM-YYYY format (common in Dutch systems)
        if (DateTime.TryParseExact(dateStr, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");

        // Try other common formats
        if (DateTime.TryParse(dateStr, out var dt2))
            return dt2.ToString("yyyy-MM-dd");

        return null;
    }
    
    [GeneratedRegex("\\D")]
    public static partial Regex MyRegex();
}