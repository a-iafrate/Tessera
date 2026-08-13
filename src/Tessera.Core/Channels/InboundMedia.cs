namespace Tessera.Core.Channels;

// FileId is the provider's own reference (Telegram's file_id) — the handler that actually
// wants the bytes still has to fetch them from the provider itself (docs/03-integrazioni.md);
// this record only carries what the update payload already told us about the file, not the
// file. FileName/MimeType are null for a Telegram photo, which carries neither — only a
// document does.
public sealed record InboundMedia(string Kind, string FileId, string? FileName, string? MimeType);
