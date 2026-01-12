namespace SpuntiniBCGateway.Services;

public enum GtinType
{
    None = 0,
    EAN13 = 13,
    EAN14 = 14
}

public static class GtinValidator
{
    /// <summary>
    /// Valideer of de invoer een geldige GTIN (EAN-13 of EAN-14) is.
    /// </summary>
    public static bool IsValidGtin(string? input, out GtinType type)
    {
        type = GtinType.None;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();
        if (!s.All(char.IsDigit))
            return false;

        if (s.Length == 13)
        {
            type = GtinType.EAN13;
            return HasValidCheckDigit(s);
        }
        else if (s.Length == 14)
        {
            type = GtinType.EAN14;
            return HasValidCheckDigit(s);
        }

        return false;
    }

    /// <summary>
    /// Valideer specifiek op EAN-13.
    /// </summary>
    public static bool IsValidEan13(string? input)
        => IsValidOfLength(input, 13);

    /// <summary>
    /// Valideer specifiek op EAN-14.
    /// </summary>
    public static bool IsValidEan14(string? input)
        => IsValidOfLength(input, 14);

    /// <summary>
    /// Berekent de checkdigit voor een GTIN string (zonder checkdigit),
    /// en retourneert de berekende waarde (0-9).
    /// </summary>
    public static int ComputeCheckDigit(string gtinWithoutCheckDigit)
    {
        if (string.IsNullOrWhiteSpace(gtinWithoutCheckDigit))
            throw new ArgumentException("Emtpy input.", nameof(gtinWithoutCheckDigit));
        if (!gtinWithoutCheckDigit.All(char.IsDigit))
            throw new ArgumentException("Only digits allowed", nameof(gtinWithoutCheckDigit));

        // We beginnen rechts (positie 1 zou de checkdigit zijn),
        // dus de rechtse van gtinWithoutCheckDigit is positie 2, etc.
        int sum = 0;
        int posFromRight = 2; // omdat checkdigit hypothetisch positie 1 is

        for (int i = gtinWithoutCheckDigit.Length - 1; i >= 0; i--)
        {
            int digit = gtinWithoutCheckDigit[i] - '0';
            int weight = (posFromRight % 2 == 0) ? 3 : 1; // even pos = 3, oneven pos = 1
            sum += digit * weight;
            posFromRight++;
        }

        int mod = sum % 10;
        int check = (10 - mod) % 10;
        return check;
    }

    /// <summary>
    /// Interne helper: valideer lengte en checkdigit.
    /// </summary>
    private static bool IsValidOfLength(string? input, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();
        if (s.Length != expectedLength || !s.All(char.IsDigit))
            return false;

        return HasValidCheckDigit(s);
    }

    /// <summary>
    /// Controleer of de laatste digit overeenstemt met de berekende checkdigit.
    /// </summary>
    private static bool HasValidCheckDigit(string gtin)
    {
        // Laatste karakter is de checkdigit
        int providedCheckDigit = gtin[gtin.Length - 1] - '0';
        string body = gtin.Substring(0, gtin.Length - 1);

        int computed = ComputeCheckDigit(body);
        return providedCheckDigit == computed;
    }
}
