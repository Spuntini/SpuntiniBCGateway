namespace SpuntiniBCGateway.Services;

public class UomHelper
{
    internal const string _defaultSystemUom = "STUKS";

    public static string? GetBcUom(string company, string? uom, string? defaultUom = _defaultSystemUom)
    {
        if (string.IsNullOrEmpty(company)) return defaultUom;
        if (string.IsNullOrEmpty(uom)) return defaultUom;

        if (company.Contains("BIEBUYCK", StringComparison.InvariantCultureIgnoreCase))
            return BiebuyckHelper.GetBcUom(uom, defaultUom);

        if (company.Contains("BELLA", StringComparison.InvariantCultureIgnoreCase))
            return BellaSiciliaHelper.GetBcUom(uom, defaultUom);

        return defaultUom;
    }

    public static int GetBcQtyPerUnitOfMeasure(string company, string? uom)
    {
        if (string.IsNullOrEmpty(company)) return 1;
        if (string.IsNullOrEmpty(uom)) return 1;

        if (company.Contains("BIEBUYCK", StringComparison.InvariantCultureIgnoreCase))
            return BiebuyckHelper.GetBcQtyPerUnitOfMeasure(uom) ?? 1;

        if (company.Contains("BELLA", StringComparison.InvariantCultureIgnoreCase))
            return BellaSiciliaHelper.GetBcQtyPerUnitOfMeasure(uom) ?? 1;

        return 1;
    }

    public static int GetBcQtyRoundingPrecision(string company, string? uom)
    {
        if (string.IsNullOrEmpty(company)) return 1;
        if (string.IsNullOrEmpty(uom)) return 1;

        if (company.Contains("BIEBUYCK", StringComparison.InvariantCultureIgnoreCase))
            return BiebuyckHelper.GetBcQtyRoundingPrecision(uom) ?? 1;

        if (company.Contains("BELLA", StringComparison.InvariantCultureIgnoreCase))
            return BellaSiciliaHelper.GetBcQtyRoundingPrecision(uom) ?? 1;

        return 1;
    }
}