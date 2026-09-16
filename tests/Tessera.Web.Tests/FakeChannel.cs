using Tessera.Core.Channels;

namespace Tessera.Web.Tests;

// A recording fake, not a mock — captures what a handler actually sent so a test can assert on
// it, same spirit as the hand-written fakes in Tessera.Core.Tests/Tessera.Data.Tests (no mock
// framework anywhere in this codebase).
internal sealed class FakeChannel : IChannel
{
    public string Name => "fake";

    public ChannelCapabilities Capabilities { get; } = new(
        SupportsGroups: true, SupportsInlineKeyboard: true, SupportsProactiveFree: true, SupportsDeepLinkPayload: true);

    public List<(ChannelAddress To, string Text)> SentTexts { get; } = [];

    public List<(ChannelAddress To, string Text, IReadOnlyList<Choice> Choices)> SentChoices { get; } = [];

    public List<(ChannelAddress To, string Text, IReadOnlyList<IReadOnlyList<Choice>> Rows)> SentGroupedChoices { get; } = [];

    public List<(ChannelAddress To, string MessageId, string Text, IReadOnlyList<IReadOnlyList<Choice>> Rows)> EditedListMessages { get; } = [];

    public Task SendTextAsync(ChannelAddress to, string text, CancellationToken ct)
    {
        SentTexts.Add((to, text));
        return Task.CompletedTask;
    }

    public Task SendChoicesAsync(ChannelAddress to, string text, IReadOnlyList<Choice> choices, CancellationToken ct)
    {
        SentChoices.Add((to, text, choices));
        return Task.CompletedTask;
    }

    public Task SendGroupedChoicesAsync(ChannelAddress to, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct)
    {
        SentGroupedChoices.Add((to, text, rows));
        return Task.CompletedTask;
    }

    public Task EditListMessageAsync(ChannelAddress to, string messageId, string text, IReadOnlyList<IReadOnlyList<Choice>> rows, CancellationToken ct)
    {
        EditedListMessages.Add((to, messageId, text, rows));
        return Task.CompletedTask;
    }

    public Task SendPhotoAsync(ChannelAddress to, string photoUrl, string? caption, CancellationToken ct) =>
        throw new NotSupportedException("Not used by any shopping handler test.");

    public Task SendDocumentAsync(ChannelAddress to, string fileUrl, string fileName, string? caption, CancellationToken ct) =>
        throw new NotSupportedException("Not used by any shopping handler test.");

    public Task<Stream> DownloadMediaAsync(string fileId, CancellationToken ct) =>
        throw new NotSupportedException("Not used by any shopping handler test.");
}
