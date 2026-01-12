using System.Globalization;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public partial class BellaSiciliaHelper
{
    public static Dictionary<double, string> GetBcVatBusPostingGroupMapping(IConfiguration config, string company)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

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

    public static string GetBcItemNumberFromBellaSiciliaItemNumber(IConfiguration config, string company, string bsItemNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bsItemNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        ArgumentNullException.ThrowIfNull(config);

        string itemNumberPrefix = config[$"Companies:{company}:ItemData:ItemNumberPrefix"] ?? "BS";

        if (bsItemNumber.Length >= 18)
        {
            if (bsItemNumber.StartsWith("0000000000000000000"))
            {
                bsItemNumber = bsItemNumber.Replace("0000000000000000000", "20x0+");
            }
            else
            {
                bsItemNumber = bsItemNumber[2..];
            }
        }

        return itemNumberPrefix + bsItemNumber;
    }

    public static string? GetBcUom(string uom, string? defaultUom = UomHelper._defaultSystemUom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return defaultUom;

        return uom.ToUpperInvariant() switch
        {
            "PCE" => "STUKS",
            "STUKS" or "KG" or "L" => uom,// valid UOM codes
            "GR" => "KG",
            "PC" => "STUK",
            "LITER" => "L",
            _ => defaultUom,
        };
    }

    public static int? GetBcQtyPerUnitOfMeasure(string uom)
    {
        if (string.IsNullOrWhiteSpace(uom)) return 1;

        return uom.ToUpperInvariant() switch
        {
            "STUKS" or "PCE" or "PC" => 1,
            "KG" or "L" or "LITER" => 100,
            "GR" => 1000,
            "KARTON" => -1,
            _ => 1,
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