using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Tessera.Channels;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Data;
using Tessera.Web.Components;
using Tessera.Web.Components.Account;
using Tessera.Web.Endpoints;
using Tessera.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddScoped<UserProvisioningService>();
builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();

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
    builder.Services.AddHostedService<MessageProcessor>();
}

var app = builder.Build();

if (!telegramEnabled)
{
    app.Logger.LogWarning("Telegram:BotToken is not configured — the bot pipeline is disabled.");
}

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

app.Run();
