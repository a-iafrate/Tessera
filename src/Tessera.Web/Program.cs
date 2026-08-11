using System.Globalization;
using System.Threading.RateLimiting;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Telegram.Bot;
using Telegram.Bot.Types;
using Tessera.Ai.Llm;
using Tessera.Ai.Routing;
using Tessera.Ai.Routing.Matchers;
using Tessera.Channels;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Data;
using Tessera.Integrations;
using Tessera.Web.Components;
using Tessera.Web.Components.Account;
using Tessera.Web.Endpoints;
using Tessera.Web.Jobs;
using Tessera.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// KnownProxies/KnownNetworks stay empty on purpose: Azure App Service's front-end isn't a
// fixed, listable IP, and it's the only thing that can reach the app's network path here.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Custom metrics from day one (docs/05-ottimizzazioni.md): router level distribution, tokens
// per turn, not-understood rate. Unlike Telegram/AzureOpenAI, this SDK throws at startup if
// registered with no connection string configured — so it's registered only when one exists;
// MessageProcessor/LlmFallbackClient take TelemetryClient as optional and skip tracking
// without it, the same shape as the other optional pieces here.
var applicationInsightsEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"])
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"));
if (applicationInsightsEnabled)
{
    builder.Services.AddApplicationInsightsTelemetry();
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies(cookies =>
    {
        // HttpOnly + Secure + SameSite=Lax: the console never needs script access to the
        // auth cookie, and OAuth external-login callbacks are top-level GET redirects.
        cookies.ApplicationCookie?.Configure(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
    });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<TesseraDbContext>(options => options.UseSqlServer(connectionString));

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // No email sender is wired up yet: confirmation/reset flows are out of scope
        // until Fase 1's email infrastructure is decided.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<TesseraDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddLocalization();

// Console language follows User.PreferredCulture once someone's signed in — the same
// property the bot writes via /language, so the two channels never disagree. Anonymous
// requests (landing page, login) have no user row to read, so they fall through to
// Accept-Language (docs/09-localizzazione.md).
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    CultureInfo[] supportedCultures = [new("en"), new("it")];
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new AuthenticatedUserRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});

// Economic safety net (docs/07-compliance.md): caps DB/LLM cost from a loop bug or a
// bad-faith user, per raw channel identity — not HTTP middleware, since the identity comes
// from the webhook payload, not a header (see MessageProcessor.ProcessAsync).
builder.Services.AddSingleton(PartitionedRateLimiter.Create<string, string>(key =>
    RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 60,
        Window = TimeSpan.FromHours(1),
    })));

builder.Services.AddScoped<UserProvisioningService>();
builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
builder.Services.AddScoped<IMembershipRepository, MembershipRepository>();
builder.Services.AddScoped<IAccessPolicy, AccessPolicy>();
builder.Services.AddScoped<ShoppingListService>();
builder.Services.AddScoped<LinkService>();
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<NoteService>();
builder.Services.AddScoped<RecurringExpenseService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<DigestService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<SpaceResolver>();
builder.Services.AddScoped<SpaceService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<ActorNameResolver>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<OnboardingService>();
builder.Services.AddScoped<UndoService>();
builder.Services.AddSingleton(new IntentRouter(Matchers.All));

// L3 fallback (docs/05-ottimizzazioni.md) — optional, like the Telegram pipeline below: the
// console and the deterministic L1/L2 paths work without it. MessageProcessor's LlmFallbackClient
// parameter defaults to null when nothing is registered, degrading to the honest
// "I didn't understand" reply rather than failing to start.
var azureOpenAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
var azureOpenAiApiKey = builder.Configuration["AzureOpenAI:ApiKey"];
var azureOpenAiDeployment = builder.Configuration["AzureOpenAI:Deployment"];
var azureOpenAiEnabled = !string.IsNullOrWhiteSpace(azureOpenAiEndpoint)
    && !string.IsNullOrWhiteSpace(azureOpenAiApiKey)
    && !string.IsNullOrWhiteSpace(azureOpenAiDeployment);
if (azureOpenAiEnabled)
{
    builder.Services.AddSingleton(new AzureOpenAIClient(
        new Uri(azureOpenAiEndpoint!), new AzureKeyCredential(azureOpenAiApiKey!)));
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<AzureOpenAIClient>().GetChatClient(azureOpenAiDeployment));
    builder.Services.AddSingleton<LlmFallbackClient>();
}

// Refresh tokens may only ever live in Key Vault, never the database (hard rule 4,
// docs/07-compliance.md) — so calendar linking has no fallback path the way L3/Telegram do;
// without a vault, ITokenVault simply isn't registered and Google linking can't proceed.
var keyVaultName = builder.Configuration["KeyVault:Name"];
var keyVaultEnabled = !string.IsNullOrWhiteSpace(keyVaultName);
if (keyVaultEnabled)
{
    builder.Services.AddSingleton(new SecretClient(
        new Uri($"https://{keyVaultName}.vault.azure.net/"), new DefaultAzureCredential()));
    builder.Services.AddScoped<ITokenVault, KeyVaultTokenVault>();
}

// Google Calendar linking (docs/02-modello-dati.md, docs/03-integrazioni.md) — optional like
// the other external integrations here; the console and bot work fully without it.
var googleClientId = builder.Configuration["Google:ClientId"];
var googleClientSecret = builder.Configuration["Google:ClientSecret"];
var googleCalendarEnabled = keyVaultEnabled
    && !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret);
if (googleCalendarEnabled)
{
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<ICalendarProvider, GoogleCalendarClient>();
    builder.Services.AddScoped<LinkedAccountService>();
    builder.Services.AddScoped<CalendarSpaceService>();
    builder.Services.AddScoped<CalendarQueryService>();
}

// The bot pipeline is only wired up once a bot token is configured, so the console works
// standalone during development before a Telegram bot exists (dotnet user-secrets set
// "Telegram:BotToken" ... / "Telegram:WebhookSecret" ..., see docs/08-setup-sviluppo.md).
var telegramBotToken = builder.Configuration["Telegram:BotToken"];
var telegramEnabled = !string.IsNullOrWhiteSpace(telegramBotToken);
if (telegramEnabled)
{
    builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegramBotToken!));
    builder.Services.AddSingleton<IChannel, TelegramChannel>();
    builder.Services.AddSingleton<MessageQueue>();
    builder.Services.AddScoped<TelegramUpdateIngestor>();
    builder.Services.AddHostedService<MessageProcessor>();

    // Long polling instead of the webhook while developing locally — no ngrok tunnel to
    // manage, and the debugger attaches normally (docs/08-setup-sviluppo.md). Use a
    // separate BotFather bot for this: sharing a token with the deployed webhook means
    // one of the two silently stops receiving updates.
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddHostedService<TelegramPollingReceiver>();
    }

    // The proactive worker (docs/01-architettura.md) — reminders due, daily digest,
    // recurring-expense generation. Singleton, since SchedulerWorker resolves
    // IEnumerable<IScheduledJob> once at construction; each job opens its own scope per run.
    builder.Services.AddSingleton<IScheduledJob, RemindersDueJob>();
    builder.Services.AddSingleton<IScheduledJob, DailyDigestJob>();
    builder.Services.AddSingleton<IScheduledJob, RecurringExpenseJob>();
    builder.Services.AddHostedService<SchedulerWorker>();
}

var app = builder.Build();

if (!applicationInsightsEnabled)
{
    app.Logger.LogWarning("Application Insights is not configured — custom metrics won't be collected.");
}

if (!keyVaultEnabled)
{
    app.Logger.LogWarning("KeyVault:Name is not configured — Google Calendar linking is disabled.");
}
else if (!googleCalendarEnabled)
{
    app.Logger.LogWarning("Google:ClientId/ClientSecret are not configured — Google Calendar linking is disabled.");
}

if (!azureOpenAiEnabled)
{
    app.Logger.LogWarning("AzureOpenAI configuration is missing — the L3 fallback is disabled.");
}

if (!telegramEnabled)
{
    app.Logger.LogWarning("Telegram:BotToken is not configured — the bot pipeline is disabled.");
}
else
{
    // Command names are canonical English and identical across languages — only the menu
    // descriptions are localized (docs/09-localizzazione.md: a mixed-language group must see
    // the same command names, or a command copied between members stops working).
    await using var commandsScope = app.Services.CreateAsyncScope();
    var botClient = commandsScope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
    var commandsLocalizer = commandsScope.ServiceProvider.GetRequiredService<IStringLocalizer<Messages>>();

    BotCommand[] BuildCommands(string cultureName)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
        return
        [
            new("list", commandsLocalizer["Commands.List.Description"]),
            new("expense", commandsLocalizer["Commands.Expense.Description"]),
            new("remind", commandsLocalizer["Commands.Remind.Description"]),
            new("note", commandsLocalizer["Commands.Note.Description"]),
            new("month", commandsLocalizer["Commands.Month.Description"]),
            new("link", commandsLocalizer["Commands.Link.Description"]),
            new("language", commandsLocalizer["Commands.Language.Description"]),
            new("help", commandsLocalizer["Commands.Help.Description"]),
        ];
    }

    await botClient.SetMyCommands(BuildCommands("en"));
    await botClient.SetMyCommands(BuildCommands("it"), languageCode: "it");
}

// Azure App Service (Windows/IIS) terminates TLS at its front-end and forwards requests as
// plain HTTP with X-Forwarded-Proto — without this, HttpContext.Request.IsHttps is wrong
// inside the app, which silently confuses cookies, antiforgery and the SignalR circuit
// negotiation. Must run before UseHttpsRedirection/UseAuthentication.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// Webhooks are machine-to-machine: a 401/404 there must stay that status code, not be
// rewritten into the Blazor "/not-found" page (whose own antiforgery check then rejects
// the JSON body and masks the original status with a 400).
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/hooks"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();

// After UseAuthentication so HttpContext.User is already the signed-in principal by the time
// AuthenticatedUserRequestCultureProvider reads it.
app.UseRequestLocalization();

app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

if (telegramEnabled)
{
    app.MapTelegramWebhook();
}

if (googleCalendarEnabled)
{
    app.MapGoogleCalendarEndpoints();
}

app.Run();
