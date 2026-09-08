namespace Tessera.Core.Channels;

// The dedup row hard rule 6 requires (docs/01-architettura.md) doubles as this message's
// replay record for the in-memory queue's one known gap (docs/01-architettura.md, "vincolo
// noto: coda in memoria"): a restart between TelegramUpdateIngestor inserting this row and
// MessageProcessor finishing the message loses it from the queue, and once the webhook has
// returned 200 OK, Telegram never redelivers it on its own. PendingMessageRecoveryJob
// (Tessera.Web) re-enqueues rows where CompletedAt is still null well after ProcessedAt.
//
// CompletedAt/PayloadJson stay null for the PayPal webhook's use of this same table
// (docs/03-integrazioni.md) — that path processes synchronously within the request, so there's
// nothing to replay and no completion to mark.
public class ProcessedMessage
{
    public string ChannelName { get; set; } = null!;
    public string ProviderMessageId { get; set; } = null!;
    public DateTimeOffset ProcessedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? PayloadJson { get; set; }
}
