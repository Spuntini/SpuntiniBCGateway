using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public static partial class StringHelper
{


    // Tries to get a value from the dictionary by key, ignoring case.
    public static Encoding GetEncoding(string encodingName)
    {
        Encoding encoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(encodingName))
        {
            if (encodingName.Equals("ANSI", StringComparison.OrdinalIgnoreCase))
            {                
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(1252);
            }
            else if (encodingName.StartsWith("UNI", StringComparison.OrdinalIgnoreCase))
            {
                encoding = Encoding.Unicode;
            }
            else if (encodingName.Equals("ASCII", StringComparison.OrdinalIgnoreCase))
            {
                encoding = Encoding.ASCII;
            }
            else if (encodingName.Equals("UTF32", StringComparison.OrdinalIgnoreCase) || encodingName.Equals("UTF-32", StringComparison.OrdinalIgnoreCase))
            {
                encoding = Encoding.UTF32;
            }
        }

        return encoding;
    }

    public static string GetDurationString(TimeSpan ts)
    {
        return ts.TotalMilliseconds switch
        {
            < 1 => $"{ts.TotalMilliseconds * 1000:N0}µs",
            < 1000 => $"{ts.TotalMilliseconds:N0}ms",
            < 60000 => $"{ts.Seconds}s {ts.Milliseconds}ms",
            < 3600000 => $"{ts.Minutes}m {ts.Seconds}s {ts.Milliseconds}ms",
            _ => $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s {ts.Milliseconds}ms",
        };
    }

    public static string CleanUpString(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        while (text.Contains("  ")) text = text.Replace("  ", " ");

        text = text.ReplaceLineEndings();
        text = text.Replace("/", "");
        if (text.EndsWith(" O", StringComparison.InvariantCultureIgnoreCase))
            text = text[..^2];

        return text;
    }

    // Normalize a decimal number string so it can be parsed using the provided culture.
    // Handles inputs like "1.234,56" or "1,234.56" by detecting separators and
    // replacing/removing group separators and mapping the decimal separator to the
    // provided culture's NumberDecimalSeparator.
    public static string NormalizeDecimalStringForCulture(string? input, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        input = input.Trim();

        // remove non-breaking spaces and normal spaces
        input = input.Replace(((char)0x00A0).ToString(), "");
        input = input.Replace(" ", string.Empty);

        // keep only characters that are relevant: digits, separators, sign and exponent
        // but we will rely on separator logic below; first remove currency symbols
        input = Regex.Replace(input, @"[^0-9\.,\-+eE]", string.Empty);

        string decimalSep = culture.NumberFormat.NumberDecimalSeparator;
        int dotCount = input.Count(c => c == '.');
        int commaCount = input.Count(c => c == ',');

        if (dotCount > 0 && commaCount > 0)
        {
            // both present: the rightmost separator is the decimal separator
            int lastDot = input.LastIndexOf('.');
            int lastComma = input.LastIndexOf(',');
            char decimalChar = lastDot > lastComma ? '.' : ',';
            char groupChar = decimalChar == '.' ? ',' : '.';

            // remove grouping chars
            input = input.Replace(groupChar.ToString(), string.Empty);
            // replace decimal char with culture decimal separator
            input = input.Replace(decimalChar.ToString(), decimalSep);
            return input;
        }

        // Only commas present
        if (commaCount > 0)
        {
            if (commaCount > 1)
            {
                // assume commas are group separators
                input = input.Replace(",", string.Empty);
                return input;
            }

            // single comma -> inspect digits after comma
            int idx = input.IndexOf(',');
            string after = input[(idx + 1)..];
            if (after.Length == 3)
            {
                // likely a thousands separator
                input = input.Replace(",", string.Empty);
                return input;
            }

            // else treat comma as decimal separator
            input = input.Replace(",", decimalSep);
            return input;
        }

        // Only dots present
        if (dotCount > 0)
        {
            if (dotCount > 1)
            {
                // assume dots are group separators
                input = input.Replace(".", string.Empty);
                return input;
            }

            int idx = input.IndexOf('.');
            string after = input[(idx + 1)..];
            if (after.Length == 3)
            {
                // likely a thousands separator
                input = input.Replace(".", string.Empty);
                return input;
            }

            // single dot -> treat as decimal separator
            input = input.Replace(".", decimalSep);
            return input;
        }

        // no separators, return digits-only string
        return input;
    }

    // Try parse a string to double using the provided culture, normalizing separators first.
    public static bool TryParseDouble(string? input, CultureInfo culture, out double value)
    {
        value = 0d;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string normalized = NormalizeDecimalStringForCulture(input, culture);
        // Use floating number style and allow exponent
        return double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, culture, out value);
    }

    public static bool TryParseInt(string? input, CultureInfo culture, out int value)
    {
        value = 0;
        bool result = TryParseDouble(input, culture, out double doubleValue);
        if (!result) return result;

        value = (int)doubleValue;
        return result;
    }

    public static string KeepOnlyNumbers(string input) => MyRegex().Replace(input, "");
    [GeneratedRegex(@"\D")]
    private static partial Regex MyRegex();

    public static bool IsTrue(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();
        return input.StartsWith("w", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("y", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("t", StringComparison.OrdinalIgnoreCase) ||
               input.Equals("1", StringComparison.OrdinalIgnoreCase);
    }
}