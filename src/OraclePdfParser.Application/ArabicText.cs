using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OraclePdfParser.Application;

public static class ArabicText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder();
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char c = raw switch {
                >= '٠' and <= '٩' => (char)('0' + raw - '٠'),
                >= '۰' and <= '۹' => (char)('0' + raw - '۰'),
                '٫' => '.', '٬' or '،' => ',', 'ـ' => ' ',
                '\u200e' or '\u200f' or '\u061c' or '\u202a' or '\u202b' or '\u202c' or '\u2066' or '\u2067' or '\u2068' or '\u2069' => ' ',
                _ => raw
            };
            builder.Append(c);
        }
        return Regex.Replace(builder.ToString().Replace("\r\n","\n").Replace('\r','\n'), @"[ \t]+", " ").Trim();
    }

    public static string Key(string text)
    {
        string value = Normalize(text);
        return Regex.Replace(value.Replace('أ','ا').Replace('إ','ا').Replace('آ','ا')
            .Replace('ى','ي').Replace('ة','ه'), @"\s+", " ").Trim();
    }
    public static IEnumerable<string> Lines(string raw) =>
        Normalize(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static bool TryMoney(string text, out decimal result) =>
        decimal.TryParse(Normalize(text).Replace(",", ""), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out result);
    public static decimal? LastMoney(string value)
    {
        var matches = Regex.Matches(Normalize(value), @"(?<![\d])\d[\d,.]*(?![\d])");
        for (int i=matches.Count-1; i>=0; i--)
            if (TryMoney(matches[i].Value, out var amount)) return amount;
        return null;
    }
}
