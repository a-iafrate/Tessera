namespace Tessera.Core.Channels;

// FileId is the provider's own reference (Telegram's file_id) — the handler that actually
// wants the bytes still has to fetch them from the provider itself (docs/03-integrazioni.md);
// this record only carries what the update payload already told us about the file, not the
// file. FileName/MimeType are null for a Telegram photo, which carries neither — only a
// document does. DurationSeconds is null for anything but a voice message
// (docs/13-piano-miglioramenti.md, E1) — trailing and optional so old ProcessedMessage.PayloadJson
// rows without it still deserialize.
public sealed record InboundMedia(string Kind, string FileId, string? FileName, string? MimeType, int? DurationSeconds = null);
