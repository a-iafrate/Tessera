using System.Globalization;

namespace Tessera.Web.Services;

// Currency belongs to the space, format belongs to the reader (docs/09-localizzazione.md):
// the same expense renders "1.234,56 €" for an Italian user and "€1,234.56" for an
// English one — same money, same currency, two conventions.
public static class MoneyFormatter
{
    public static string Format(decimal amount, string currency, string culture)
    {
        var ci = (CultureInfo)new CultureInfo(culture).Clone();
        var region = new RegionInfo(currency == "EUR" ? "IT" : currency);
        ci.NumberFormat.CurrencySymbol = region.CurrencySymbol;
        return amount.ToString("C", ci);
    }
}
