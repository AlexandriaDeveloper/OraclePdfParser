using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace OraclePdfParser.Application;
public static class Arabic {
    public static string Normalize(string? source) {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var text = source.Normalize(NormalizationForm.FormKC).Replace("\u200e","").Replace("\u200f","").Replace("\u061c","").Replace("\u00ad","");
        var b = new StringBuilder(text.Length);
        foreach (char c in text) {
            if (c >= '٠' && c <= '٩') b.Append((char)('0' + c - '٠'));
            else if (c >= '۰' && c <= '۹') b.Append((char)('0' + c - '۰'));
            else if (c is '٫') b.Append('.');
            else if (c is '،' or '٬') b.Append(',');
            else if (c is 'ـ' or '\u064b' or '\u064c' or '\u064d' or '\u064e' or '\u064f' or '\u0650' or '\u0651' or '\u0652') continue;
            else b.Append(c);
        }
        return Regex.Replace(b.ToString(), @"[ \t]+", " ").Trim();
    }
    public static string Key(string? text) => Regex.Replace(Normalize(text).Replace('أ','ا').Replace('إ','ا').Replace('آ','ا').Replace('ى','ي').Replace('ة','ه'), @"\s+", " ").Trim();
    public static decimal? Amount(string? source) {
        var s = Normalize(source).Replace(",", "").Trim();
        return Regex.IsMatch(s, @"^-?\d+(?:\.\d{1,2})?$") && decimal.TryParse(s, NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
    public static string[] Lines(string s) => Normalize(s).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
