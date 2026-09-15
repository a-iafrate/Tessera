using Microsoft.Extensions.Logging;
using OpenAI.Audio;

namespace Tessera.Ai.Llm;

// A distinct LLM surface, same shape as ReceiptVisionClient — not part of the L1/L2/L3 router,
// single purpose, swallows its own failures so the caller only ever sees "did this work"
// (docs/13-piano-miglioramenti.md, E1). Uses its own Azure OpenAI deployment (a transcription
// model, not the chat one L3/vision/recipes share), gated separately in Program.cs.
public sealed class VoiceTranscriptionClient(AudioClient audioClient, ILogger<VoiceTranscriptionClient> logger)
{
    // fileName only needs a recognized audio extension for the API to sniff the format — it's
    // never shown to anyone or stored. Telegram voice messages are OGG/Opus.
    public async Task<string?> TranscribeAsync(Stream audio, string fileName, CancellationToken ct)
    {
        try
        {
            var response = await audioClient.TranscribeAudioAsync(audio, fileName, cancellationToken: ct);
            var text = response.Value.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice transcription failed");
            return null;
        }
    }
}
