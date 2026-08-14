using System.Text.RegularExpressions;

namespace Tessera.Core.Shopping;

// Shared between ShoppingItem.NormalizedName (fuzzy match on the list) and
// ExpenseLine.NormalizedName (price history aggregation, docs/06-roadmap.md "Storico
// prezzi") — both ask "what product is this", so they share one shallow answer:
// lowercase, trim, strip the leading article. Real product-name normalization (brand/size
// variants) is deliberately out of scope (docs/02-modello-dati.md).
public static class ProductNameNormalizer
{
    private static readonly Regex LeadingArticle = new(
        @"^(il|lo|la|i|gli|le|un|uno|una|the|a|an)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string rawText) =>
        LeadingArticle.Replace(rawText.Trim().ToLowerInvariant(), "").Trim();
}
