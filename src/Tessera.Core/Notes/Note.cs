namespace Tessera.Core.Notes;

// Free text shared per space (docs/02-modello-dati.md) — the same idea as generic shopping
// lists, but a block of text rather than checkable items. Title is optional: most notes are a
// short thought that doesn't need one.
public class Note
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string? Title { get; set; }
    public string Body { get; set; } = null!;

    // No FK to User, same reasoning as ShoppingItem.AddedByUserId (docs/02-modello-dati.md,
    // hard rule 3) — the account may be deleted later; resolve names via ResolveActorName.
    public Guid CreatedByUserId { get; set; }
    public Guid? LastEditedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
