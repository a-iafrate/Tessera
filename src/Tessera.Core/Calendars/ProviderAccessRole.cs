namespace Tessera.Core.Calendars;

// What the provider itself grants on this calendar — not negotiable by any permission inside
// Tessera. A calendar shared read-only can never become writable no matter what a space's
// mapping or membership says (docs/02-modello-dati.md).
public enum ProviderAccessRole
{
    FreeBusyReader = 1,
    Reader = 2,
    Writer = 3,
    Owner = 4,
}
