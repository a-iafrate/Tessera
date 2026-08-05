namespace Tessera.Core.Expenses;

// Learned per space, not global: "Conad" can be groceries for one family and something
// else for another (docs/02-modello-dati.md). Asked once via inline keyboard, then never
// again for that merchant — this is the row that remembers the answer.
public class MerchantCategoryMapping
{
    public Guid SpaceId { get; set; }
    public string MerchantNormalized { get; set; } = null!;
    public Guid CategoryId { get; set; }
    public int ConfirmationCount { get; set; }
}
