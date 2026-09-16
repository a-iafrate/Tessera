using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Notes;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Services;

// Third of five domain handler classes to be extracted out of MessageProcessor
// (docs/13-piano-miglioramenti.md, F3). Notes went third (after Shopping and Expense) because,
// like Shopping, it carries no cross-turn ConversationState — every reminder/calendar flow
// still ahead of this one confirms an LLM-interpreted date via a pending-intent round trip;
// Notes never asks anything back.
//
// Constructed fresh per message inside MessageProcessor.ProcessAsync, not DI-registered as a
// singleton — same reasoning as ShoppingHandlers/ExpenseHandlers: MessageProcessor.channel is a
// mutable field only a per-message instance can safely capture.
public sealed class NoteHandlers(
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    Func<OnboardingService, ChannelAddress, Guid, string, string, CancellationToken, Task> finalizeReplyAsync)
{
    // /note is a native L1 command: bare lists, anything else is free text saved verbatim as
    // the body (no title) — the model-driven create_note tool is what fills in a title, when
    // the phrasing has one to extract.
    public async Task<string?> HandleNoteCommandAsync(
        AsyncServiceScope scope, NoteService notes, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, Guid userId, string argsText, CancellationToken ct)
    {
        var trimmed = argsText.Trim();
        return trimmed.Length == 0
            ? await HandleShowNotesAsync(scope, address, notes, spaceId, userId, ct)
            : await CreateNoteAndReplyAsync(notes, undo, onboarding, address, spaceId, userId, title: null, trimmed, ct);
    }

    // Sends the list itself (rather than just returning text) because notes with an
    // attachment get a button to reveal it on demand — showing every image unprompted on
    // every "what notes are there?" would get noisy fast once a space has more than a couple.
    // One message per note, not one giant list — a note's "show attachment" button then sits
    // directly under that note's own text instead of in a single wall of buttons at the end,
    // disconnected from which note each one belonged to. Every attachment gets a button
    // regardless of content type (image, PDF, ...) — HandleShowNoteAttachmentCallbackAsync
    // decides how to actually deliver it.
    public async Task<string?> HandleShowNotesAsync(
        AsyncServiceScope scope, ChannelAddress address, NoteService notes, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var all = await notes.GetNotesAsync(spaceId, userId, ct);
        if (all.Count == 0)
        {
            return localizer["Notes.ListEmpty"];
        }

        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        foreach (var note in all)
        {
            var text = note.Title is { Length: > 0 }
                ? localizer["Notes.ListItemLineTitled", note.Title, note.Body].Value
                : localizer["Notes.ListItemLine", note.Body].Value;

            List<Choice> attachmentChoices = [];
            if (attachments is not null)
            {
                var noteAttachments = await attachments.GetForAsync(ResourceKind.Notes, note.Id, ct);
                attachmentChoices = noteAttachments
                    .Select(a => new Choice(localizer["Notes.ShowAttachmentButton"].Value, $"note.showattachment:{a.Id}"))
                    .ToList();
            }

            if (attachmentChoices.Count == 0)
            {
                await channel.SendTextAsync(address, text, ct);
            }
            else
            {
                await channel.SendChoicesAsync(address, text, attachmentChoices, ct);
            }
        }

        return null;
    }

    // note.showattachment: callback — resolved lazily on tap rather than eagerly with the list
    // (HandleShowNotesAsync above), so a space with many attached notes doesn't flood the
    // chat. The attachment's own SpaceId is the permission check here, not the caller's
    // current space, since by the time this fires the user may be in any conversation context.
    // Delivered as a photo or a generic document depending on ContentType — Telegram's
    // sendPhoto rejects anything that isn't actually an image.
    public async Task HandleShowNoteAttachmentCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string attachmentIdText, CancellationToken ct)
    {
        var attachmentService = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachmentService is null || !Guid.TryParse(attachmentIdText, out var attachmentId))
        {
            return;
        }

        var attachment = await attachmentService.GetByIdAsync(attachmentId, ct);
        if (attachment is null || attachment.Resource != ResourceKind.Notes)
        {
            return;
        }

        var accessPolicy = scope.ServiceProvider.GetRequiredService<IAccessPolicy>();
        if (!await accessPolicy.CanAsync(user.Id, attachment.SpaceId, ResourceKind.Notes, AccessLevel.Read, ct))
        {
            return;
        }

        var url = await attachmentService.GetReadUrlAsync(attachment.Id, TimeSpan.FromMinutes(5), ct);
        if (url is null)
        {
            return;
        }

        if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await channel.SendPhotoAsync(address, url, attachment.FileName, ct);
        }
        else
        {
            await channel.SendDocumentAsync(address, url, attachment.FileName, caption: null, ct);
        }
    }

    // Shared by the native /note command and the create_note L3 tool — both create-and-confirm
    // the same way, undo button included (docs/10-conversazione.md).
    public async Task<string?> CreateNoteAndReplyAsync(
        NoteService notes, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, Guid userId, string? title, string body, CancellationToken ct)
    {
        var note = await notes.CreateAsync(spaceId, userId, title, body, ct);
        await undo.RecordNoteAsync(userId, spaceId, note.Id, ct);
        await finalizeReplyAsync(onboarding, address, userId, "notes", localizer["Notes.Created"].Value, ct);
        return null;
    }

    public async Task<string?> HandleLlmDeleteNoteAsync(
        AsyncServiceScope scope, NoteService notes, Guid spaceId, Guid userId, string searchText, CancellationToken ct)
    {
        var note = await notes.FindNoteAsync(spaceId, userId, searchText, ct);
        if (note is null)
        {
            return localizer["Notes.NotFound"];
        }

        await notes.DeleteAsync(spaceId, userId, note.Id, ct);
        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachments is not null)
        {
            await attachments.DeleteAllForAsync(ResourceKind.Notes, note.Id, ct);
        }

        return localizer["Notes.Deleted"];
    }

    // The only entry point into the attachment pipeline from the bot side (docs/06-roadmap.md
    // Fase 4) — a captioned photo/document becomes a new note with that attachment; an
    // uncaptioned one attaches to whatever note this user touched most recently, mirroring
    // UndoService's "most recent thing" idea but scoped to Notes since nothing else can take an
    // attachment yet.
    public async Task<string?> HandleIncomingMediaAsync(
        AsyncServiceScope scope, NoteService notes, ChannelAddress address, Guid spaceId, User user, string? caption, InboundMedia media, CancellationToken ct)
    {
        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachments is null)
        {
            return localizer["Attachments.NotConfigured"];
        }

        Note? note;
        var isNewNote = !string.IsNullOrWhiteSpace(caption);
        if (isNewNote)
        {
            note = await notes.CreateAsync(spaceId, user.Id, title: null, body: caption!, ct);
        }
        else
        {
            note = await notes.GetMostRecentByUserAsync(spaceId, user.Id, ct);
            if (note is null)
            {
                return localizer["Attachments.NoRecentNote"];
            }
        }

        using var content = await channel.DownloadMediaAsync(media.FileId, ct);
        var fileName = media.FileName ?? $"{media.Kind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var contentType = media.MimeType ?? (media.Kind == "photo" ? "image/jpeg" : "application/octet-stream");
        await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, user.Id, content, fileName, contentType, content.Length, ct);

        return isNewNote ? localizer["Attachments.NoteCreated"] : localizer["Attachments.AddedToRecentNote"];
    }
}
