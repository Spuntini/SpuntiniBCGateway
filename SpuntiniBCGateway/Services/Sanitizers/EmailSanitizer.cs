using System.Globalization;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public class EmailSanitizer
{
    // RFC-achtige set voor local-part (ASCII), pragmatisch:
    private static readonly Regex LocalAllowed = new(@"[^A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]", RegexOptions.Compiled);
    // Domein: labels gescheiden door punt, elk label: letters, digits, '-', maar geen leading/trailing '-'
    private static readonly Regex DomainAllowed = new(@"[^A-Za-z0-9\.\-]", RegexOptions.Compiled);

    public static string CleanEmail(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = input.Trim();

        // Verwijder eventuele display name en angle brackets: "Naam <user@domain>"
        var matchAngle = Regex.Match(input, @"<\s*(.+?)\s*>$");
        if (matchAngle.Success)
            input = matchAngle.Groups[1].Value;

        // Split op @ (alleen eerste @ is scheiding)
        int atIndex = input.IndexOf('@');
        if (atIndex < 1 || atIndex == input.Length - 1)
            return string.Empty; // geen geldige structuur om te schonen

        string local = input[..atIndex];
        string domain = input[(atIndex + 1)..];

        // Opschonen local-part
        local = LocalAllowed.Replace(local, "");    // verwijder ongeldige chars
        local = NormalizeDots(local);               // collapse ".." -> "."
        local = local.Trim('.');                    // geen leading/trailing dot

        // Opschonen domain (ASCII)
        domain = DomainAllowed.Replace(domain, "");
        domain = NormalizeDots(domain);
        domain = domain.Trim('.');

        // Normaliseer domein labels
        string[] labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < labels.Length; i++)
        {
            string lbl = labels[i];

            // trim leading/trailing '-' (niet toegestaan)
            lbl = lbl.Trim('-');

            // leeg label na trim? dan weg
            if (string.IsNullOrEmpty(lbl))
                continue;

            labels[i] = lbl.ToLowerInvariant();
        }
        domain = string.Join(".", labels);

        // IDN (punycode) normalisatie indien nodig
        // Als je Unicode domeinen wilt toestaan, zet ze om naar ASCII (punycode):
        try
        {
            var idn = new IdnMapping();
            domain = idn.GetAscii(domain);
        }
        catch
        {
            // als IDN faalt, laten we domein zoals het is—of geef leeg terug.
            return string.Empty;
        }

        string cleaned = $"{local}@{domain}";
        return IsValidEmail(cleaned) ? cleaned : string.Empty;
    }

    private static string NormalizeDots(string s)
        => Regex.Replace(s, @"\.+", "."); // meerdere punten naar één

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch { return false; }
    }
}