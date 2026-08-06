using System.Globalization;

namespace Tessera.Core.Expenses;

// decimal.TryParse is lenient about group sizes: "1,5" in en-US parses to 15, "1.50" in
// it-IT parses to 150 — silently, with no error. A factor-10/100 mistake on money is
// exactly the kind of bug that erodes trust in everything else (docs/09-localizzazione.md).
public static class AmountAmbiguity
{
    // Ambiguous exactly when the text uses the culture's group-separator character with no
    // decimal separator anywhere — that shape parses fine, but could equally have been
    // typed using the *other* convention's decimal point by habit.
    public static bool IsAmbiguous(string text, CultureInfo culture)
    {
        var groupSeparator = culture.NumberFormat.NumberGroupSeparator;
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;

        return text.Contains(groupSeparator, StringComparison.Ordinal)
            && !text.Contains(decimalSeparator, StringComparison.Ordinal);
    }

    // The two plausible readings: as literally typed (group separator taken at face value),
    // and with the ambiguous character reinterpreted as the user's own decimal separator.
    public static (decimal AsGrouped, decimal AsDecimal) GetCandidates(string text, CultureInfo culture)
    {
        var groupSeparator = culture.NumberFormat.NumberGroupSeparator;
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;

        var asGrouped = decimal.Parse(text, NumberStyles.Number, culture);
        var reinterpreted = text.Replace(groupSeparator, decimalSeparator);
        var asDecimal = decimal.Parse(reinterpreted, NumberStyles.Number, culture);

        return (asGrouped, asDecimal);
    }
}
