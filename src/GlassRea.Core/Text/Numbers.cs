using System.Globalization;

namespace GlassRea.Core.Text;

public static class Numbers
{
    /// <summary>
    /// Parses a decimal typed on any keyboard: Arabic-Indic (٠-٩) and Persian (۰-۹) digits become 0-9,
    /// and the Arabic decimal separator or a comma becomes a dot.
    /// </summary>
    public static bool TryParse(string text, out double value)
    {
        var chars = text.Trim().Select(ch => ch switch
        {
            >= '٠' and <= '٩' => (char)('0' + ch - '٠'),
            >= '۰' and <= '۹' => (char)('0' + ch - '۰'),
            '٫' or ',' => '.',
            _ => ch,
        }).ToArray();
        return double.TryParse(new string(chars), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
