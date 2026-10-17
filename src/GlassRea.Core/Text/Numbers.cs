using System.Globalization;

namespace GlassRea.Core.Text;

public static class Numbers
{
    /// <summary>
    /// Parses a decimal typed on any keyboard: Arabic-Indic (\u0660-\u0669) and Persian (\u06F0-\u06F9) digits become 0-9,
    /// and the Arabic decimal separator or a comma becomes a dot.
    /// </summary>
    public static bool TryParse(string text, out double value)
    {
        var chars = text.Trim().Select(ch => ch switch
        {
            >= '\u0660' and <= '\u0669' => (char)('0' + ch - '\u0660'),
            >= '\u06F0' and <= '\u06F9' => (char)('0' + ch - '\u06F0'),
            '٫' or ',' => '.',
            _ => ch,
        }).ToArray();
        return double.TryParse(new string(chars), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
