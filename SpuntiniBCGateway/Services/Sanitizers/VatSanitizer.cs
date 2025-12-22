using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public partial class VatSanitizer
{
    /// <summary>
    /// Verwijdert alles behalve A-Z en 0-9, forceert uppercase country code als aanwezig.
    /// Optioneel kan je een defaultCountryCode (bv. "BE") meegeven als er geen code is.
    /// </summary>
    public static string Clean(string input, string? defaultCountryCode = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Alles naar uppercase, spaties/teken weg
        string raw = MyRegex().Replace(input.ToUpperInvariant(), "");

        // Als het begint met 2 letters, beschouwen als landcode
        if (raw.Length >= 2 && raw.Take(2).All(char.IsLetter))
            return raw; // bv. "BE0123456789"

        // Geen landcode aanwezig
        if (!string.IsNullOrEmpty(defaultCountryCode))
            return defaultCountryCode.ToUpperInvariant() + raw;

        return raw;
    }

    [GeneratedRegex(@"[^A-Z0-9]")]
    private static partial Regex MyRegex();
}


public static partial class BeVatValidator
{
    private static readonly Regex BePattern = MyRegex();

    public static bool IsValid(string input)
    {
        string vat = VatSanitizer.Clean(input, "BE");
        if (!BePattern.IsMatch(vat)) return false;

        string digits = vat[2..]; // 10 cijfers
        // Probeer beide varianten (8 of 9 cijfers voor checksum-basis)
        return CheckMod97(digits, 8) || CheckMod97(digits, 9);
    }

    private static bool CheckMod97(string digits, int significant)
    {
        if (digits.Length != 10) return false;
        string head = digits[..significant];
        string tail = digits.Substring(10 - 2, 2);

        if (!ulong.TryParse(head, out ulong headNum)) return false;
        if (!int.TryParse(tail, out int checksum)) return false;

        int calc = 97 - (int)(headNum % 97);
        if (calc == 0) calc = 97;

        return calc == checksum;
    }

    [GeneratedRegex(@"^BE[0-9]{10}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}


public interface IVatCountryValidator
{
    bool MatchesFormat(string vat);
    bool IsValid(string vat); // mag format+checksum doen
}

public partial class BeCountryValidator : IVatCountryValidator
{
    private static readonly Regex Pattern = MyRegex();
    public bool MatchesFormat(string vat) => Pattern.IsMatch(vat);
    public bool IsValid(string vat) => BeVatValidator.IsValid(vat);
    [GeneratedRegex(@"^BE[0-9]{10}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

public partial class DeCountryValidator : IVatCountryValidator
{
    // Duitsland: DE + 9 cijfers (checksum bestaat, niet geïmplementeerd hier)
    private static readonly Regex Pattern = MyRegex();
    public bool MatchesFormat(string vat) => Pattern.IsMatch(vat);
    public bool IsValid(string vat) => MatchesFormat(vat); // TODO: checksum
    [GeneratedRegex(@"^DE[0-9]{9}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

public partial class FrCountryValidator : IVatCountryValidator
{
    // Frankrijk: FR + 2 alfanumerieke controle + 9 cijfers (SIREN)
    private static readonly Regex Pattern = MyRegex();
    public bool MatchesFormat(string vat) => Pattern.IsMatch(vat);
    public bool IsValid(string vat) => MatchesFormat(vat); // TODO: checksum
    [GeneratedRegex(@"^FR[0-9A-Z]{2}[0-9]{9}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

public partial class NlCountryValidator : IVatCountryValidator
{
    // Nederland (nieuw BTW-id 2020+): NL + 9 cijfers + B + 2 cijfers (B01..B99)
    private static readonly Regex Pattern = MyRegex();
    public bool MatchesFormat(string vat) => Pattern.IsMatch(vat);
    public bool IsValid(string vat) => MatchesFormat(vat); // TODO: 11-proef variant voor BSN (oude), niet toepassen op nieuw BTW-id
    [GeneratedRegex(@"^NL[0-9]{9}B[0-9]{2}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

public static class EuVatValidator
{
    private static readonly Dictionary<string, IVatCountryValidator> _byCountry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BE"] = new BeCountryValidator(),
            ["DE"] = new DeCountryValidator(),
            ["FR"] = new FrCountryValidator(),
            ["NL"] = new NlCountryValidator(),
            // ... voeg andere landen toe
        };

    /// <summary>
    /// Probeert landcode af te leiden uit string. Als afwezig kun je defaultCountryCode meegeven.
    /// </summary>
    public static bool TryValidate(string input, out string normalizedVat, string? defaultCountryCode = null)
    {
        normalizedVat = VatSanitizer.Clean(input, defaultCountryCode);
        if (normalizedVat.Length < 4) return false; // minimaal CC + cijfers

        string cc = normalizedVat[..2];
        if (!_byCountry.TryGetValue(cc, out var country)) return false;

        return country.IsValid(normalizedVat);
    }    
}
