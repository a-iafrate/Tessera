using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// Third extracted domain handler (docs/13-piano-miglioramenti.md, F3). Same testing shape as
// ShoppingHandlersTests/ExpenseHandlersTests: real domain services against an in-memory SQLite
// database, a hand-written FakeChannel, a real IStringLocalizer<Messages>. AttachmentService
// needs an AsyncServiceScope (it's an optional dependency, resolved via
// scope.ServiceProvider.GetService in every note handler that touches it) — CreateScope below
// builds a minimal DI container around the same TesseraDbContext instance rather than a second
// in-memory database, so an attachment written in one call is visible to the next.
public class NoteHandlersTests : IDisposable
{
    private readonly TestWebDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly NoteService notes;
    private readonly UndoService undo;
    private readonly OnboardingService onboarding;
    private readonly FakeBlobStorage blobStorage = new();
    private readonly FakeChannel channel = new();
    private readonly IStringLocalizer<Messages> localizer;
    private readonly List<(Guid UserId, string FeatureKey, string BaseReply)> finalizedReplies = [];

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly User user;
    private readonly ChannelAddress address = new("fake", "chat-1");

    public NoteHandlersTests()
    {
        var accessPolicy = new TestWebDatabase.AllowAllAccessPolicy();
        notes = new NoteService(Db, accessPolicy);
        undo = new UndoService(Db);
        onboarding = new OnboardingService(Db);
        localizer = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();

        user = new User { Id = userId, Email = "a@example.com", PreferredCulture = "en" };
        Db.DomainUsers.Add(user);
        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    private NoteHandlers CreateHandlers() => new(channel, localizer, (o, a, u, featureKey, baseReply, ct) =>
    {
        finalizedReplies.Add((u, featureKey, baseReply));
        return Task.CompletedTask;
    });

    // AttachmentService is resolved from the scope, not passed as a parameter — registering it
    // only when a test needs one exercises the same "not configured" branch every handler has.
    private AsyncServiceScope CreateScope(bool withAttachments)
    {
        var services = new ServiceCollection().AddSingleton<IAccessPolicy>(new TestWebDatabase.AllowAllAccessPolicy());
        if (withAttachments)
        {
            services.AddSingleton(new AttachmentService(Db, blobStorage));
        }

        return services.BuildServiceProvider().CreateAsyncScope();
    }

    [Fact]
    public async Task HandleNoteCommandAsync_WithText_CreatesANote_AndFinalizesReply()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);

        var result = await handlers.HandleNoteCommandAsync(scope, notes, undo, onboarding, address, spaceId, userId, "wifi password is hunter2", CancellationToken.None);

        Assert.Null(result); // already sent via finalizeReplyAsync
        var created = Assert.Single(await notes.GetNotesAsync(spaceId, userId, CancellationToken.None));
        Assert.Equal("wifi password is hunter2", created.Body);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("notes", finalized.FeatureKey);
        Assert.Equal("Note saved.", finalized.BaseReply);
    }

    [Fact]
    public async Task HandleNoteCommandAsync_Empty_ShowsTheExistingNotes()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);
        await notes.CreateAsync(spaceId, userId, title: null, "existing note", CancellationToken.None);

        var result = await handlers.HandleNoteCommandAsync(scope, notes, undo, onboarding, address, spaceId, userId, "   ", CancellationToken.None);

        Assert.Null(result); // already sent
        var sent = Assert.Single(channel.SentTexts);
        Assert.Equal("📝 existing note", sent.Text);
    }

    [Fact]
    public async Task HandleShowNotesAsync_ReturnsEmptyMessage_WhenThereAreNoNotes()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);

        var result = await handlers.HandleShowNotesAsync(scope, address, notes, spaceId, userId, CancellationToken.None);

        Assert.Equal("No notes yet", result);
        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task HandleShowNotesAsync_SendsPlainText_WhenTheNoteHasNoAttachment()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        await notes.CreateAsync(spaceId, userId, title: null, "no attachment here", CancellationToken.None);

        var result = await handlers.HandleShowNotesAsync(scope, address, notes, spaceId, userId, CancellationToken.None);

        Assert.Null(result);
        var sent = Assert.Single(channel.SentTexts);
        Assert.Equal("📝 no attachment here", sent.Text);
        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task HandleShowNotesAsync_SendsAShowAttachmentButton_WhenTheNoteHasOne()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var note = await notes.CreateAsync(spaceId, userId, title: null, "has a photo", CancellationToken.None);
        var attachments = new AttachmentService(Db, blobStorage);
        using var content = new MemoryStream([1, 2, 3]);
        await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, userId, content, "photo.jpg", "image/jpeg", 3, CancellationToken.None);

        var result = await handlers.HandleShowNotesAsync(scope, address, notes, spaceId, userId, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(channel.SentTexts);
        var sent = Assert.Single(channel.SentChoices);
        Assert.Equal("📝 has a photo", sent.Text);
        Assert.Single(sent.Choices);
    }

    [Fact]
    public async Task HandleShowNoteAttachmentCallbackAsync_SendsAPhoto_ForImageContentType()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var note = await notes.CreateAsync(spaceId, userId, title: null, "body", CancellationToken.None);
        var attachments = new AttachmentService(Db, blobStorage);
        using var content = new MemoryStream([1, 2, 3]);
        var attachment = await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, userId, content, "photo.jpg", "image/jpeg", 3, CancellationToken.None);

        await handlers.HandleShowNoteAttachmentCallbackAsync(scope, address, user, attachment.Id.ToString(), CancellationToken.None);

        var sent = Assert.Single(channel.SentPhotos);
        Assert.Equal(blobStorage.ReadUrl, sent.PhotoUrl);
    }

    [Fact]
    public async Task HandleShowNoteAttachmentCallbackAsync_SendsADocument_ForNonImageContentType()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var note = await notes.CreateAsync(spaceId, userId, title: null, "body", CancellationToken.None);
        var attachments = new AttachmentService(Db, blobStorage);
        using var content = new MemoryStream([1, 2, 3]);
        var attachment = await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, userId, content, "receipt.pdf", "application/pdf", 3, CancellationToken.None);

        await handlers.HandleShowNoteAttachmentCallbackAsync(scope, address, user, attachment.Id.ToString(), CancellationToken.None);

        var sent = Assert.Single(channel.SentDocuments);
        Assert.Equal(blobStorage.ReadUrl, sent.FileUrl);
    }

    [Fact]
    public async Task HandleShowNoteAttachmentCallbackAsync_NoOps_WhenAttachmentServiceIsNotRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);

        await handlers.HandleShowNoteAttachmentCallbackAsync(scope, address, user, Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.Empty(channel.SentPhotos);
        Assert.Empty(channel.SentDocuments);
    }

    [Fact]
    public async Task HandleLlmDeleteNoteAsync_ReturnsNotFound_WhenNoNoteMatches()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);

        var result = await handlers.HandleLlmDeleteNoteAsync(scope, notes, spaceId, userId, "wifi", CancellationToken.None);

        Assert.Equal("Couldn't find a note matching that.", result);
    }

    [Fact]
    public async Task HandleLlmDeleteNoteAsync_DeletesTheNote_AndItsAttachments()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var note = await notes.CreateAsync(spaceId, userId, title: null, "wifi password is hunter2", CancellationToken.None);
        var attachments = new AttachmentService(Db, blobStorage);
        using var content = new MemoryStream([1, 2, 3]);
        await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, userId, content, "photo.jpg", "image/jpeg", 3, CancellationToken.None);

        var result = await handlers.HandleLlmDeleteNoteAsync(scope, notes, spaceId, userId, "wifi", CancellationToken.None);

        Assert.Equal("Note deleted.", result);
        Assert.Empty(await notes.GetNotesAsync(spaceId, userId, CancellationToken.None));
        Assert.Empty(await attachments.GetForAsync(ResourceKind.Notes, note.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleIncomingMediaAsync_ReturnsNotConfigured_WhenNoAttachmentServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: false);
        var media = new InboundMedia("photo", "file-1", "photo.jpg", "image/jpeg");

        var result = await handlers.HandleIncomingMediaAsync(scope, notes, address, spaceId, user, caption: "a caption", media, CancellationToken.None);

        Assert.Equal("Attachments aren't available yet.", result);
    }

    [Fact]
    public async Task HandleIncomingMediaAsync_CreatesANewNote_WhenCaptioned()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var media = new InboundMedia("photo", "file-1", "photo.jpg", "image/jpeg");

        var result = await handlers.HandleIncomingMediaAsync(scope, notes, address, spaceId, user, caption: "receipt from Conad", media, CancellationToken.None);

        Assert.Equal("Saved as a new note with your attachment.", result);
        var created = Assert.Single(await notes.GetNotesAsync(spaceId, userId, CancellationToken.None));
        Assert.Equal("receipt from Conad", created.Body);
    }

    [Fact]
    public async Task HandleIncomingMediaAsync_AttachesToTheMostRecentNote_WhenUncaptioned()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var note = await notes.CreateAsync(spaceId, userId, title: null, "existing note", CancellationToken.None);
        var media = new InboundMedia("photo", "file-1", "photo.jpg", "image/jpeg");

        var result = await handlers.HandleIncomingMediaAsync(scope, notes, address, spaceId, user, caption: null, media, CancellationToken.None);

        Assert.Equal("Added to your most recent note.", result);
        var attachments = new AttachmentService(Db, blobStorage);
        Assert.Single(await attachments.GetForAsync(ResourceKind.Notes, note.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleIncomingMediaAsync_ReturnsNoRecentNote_WhenUncaptionedAndNoneExists()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withAttachments: true);
        var media = new InboundMedia("photo", "file-1", "photo.jpg", "image/jpeg");

        var result = await handlers.HandleIncomingMediaAsync(scope, notes, address, spaceId, user, caption: null, media, CancellationToken.None);

        Assert.Equal(
            "I don't have a recent note to attach that to — send it again with a caption to create a new note, or add one first.",
            result);
    }

    // Records what was uploaded, and serves the same fixed read URL back — a recording fake,
    // not a mock, same spirit as FakeChannel.
    private sealed class FakeBlobStorage : IBlobStorage
    {
        public string ReadUrl { get; } = "https://blob.test/fake-read-url";

        public List<string> UploadedBlobNames { get; } = [];

        public Task UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
        {
            UploadedBlobNames.Add(blobName);
            return Task.CompletedTask;
        }

        public Task<string> GetReadUrlAsync(string blobName, TimeSpan validFor, CancellationToken ct) => Task.FromResult(ReadUrl);

        public Task DeleteAsync(string blobName, CancellationToken ct)
        {
            UploadedBlobNames.Remove(blobName);
            return Task.CompletedTask;
        }
    }
}
