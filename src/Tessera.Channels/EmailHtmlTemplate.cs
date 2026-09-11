using System.Net;
using System.Text;

namespace Tessera.Channels;

// Same inline-styled, table-based layout as ForgotPassword.razor's password-reset email — hand
// -copied hex values from tokens.css rather than var(--token), because mail clients (Outlook
// desktop chief among them) don't reliably support CSS custom properties, and no external
// stylesheet is fetched at all (docs/12-stile-sito.md's design tokens exist for the web
// console; this is the one place they get duplicated as literals on purpose). Table-based
// centering is deliberate for the same reason: the safest layout primitive across mail clients.
internal static class EmailHtmlTemplate
{
    private const string Background = "#ECEAE3";
    private const string CardBackground = "#F7F5EF";
    private const string Border = "#D7D3C6";
    private const string Clay = "#B5502E";
    private const string ClayDark = "#A24829";
    private const string Ink = "#221E1C";
    private const string InkSoft = "#6B665D";

    public static string BuildDigest(
        string subject, IReadOnlyList<(string Header, string Body)> sections,
        string unsubscribeUrl, string unsubscribeText, string lang)
    {
        var sectionsHtml = new StringBuilder();
        foreach (var (header, body) in sections)
        {
            var bodyHtml = string.Join("<br/>", body.Split('\n').Select(WebUtility.HtmlEncode));
            sectionsHtml.Append($"""
                <tr>
                    <td style="padding: 16px 32px 0 32px;">
                        <div style="font-size: 15px; font-weight: 600; color: {Ink};">{WebUtility.HtmlEncode(header)}</div>
                        <div style="font-size: 14px; line-height: 1.5; color: {InkSoft}; margin-top: 4px;">{bodyHtml}</div>
                    </td>
                </tr>
                """);
        }

        var encodedUnsubscribeUrl = WebUtility.HtmlEncode(unsubscribeUrl);

        return $$"""
            <!DOCTYPE html>
            <html lang="{{lang}}">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{WebUtility.HtmlEncode(subject)}}</title>
            </head>
            <body style="margin: 0; padding: 0; background-color: {{Background}}; font-family: 'Inter', -apple-system, 'Segoe UI', Arial, sans-serif;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color: {{Background}};">
                    <tr>
                        <td align="center" style="padding: 32px 16px;">
                            <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width: 480px; width: 100%; background-color: {{CardBackground}}; border: 1px solid {{Border}}; border-radius: 14px;">
                                <tr>
                                    <td style="padding: 32px 32px 0 32px;">
                                        <span style="font-family: Georgia, 'Iowan Old Style', serif; font-size: 20px; font-weight: 600; color: {{Clay}};">Tessera</span>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 20px 32px 0 32px; font-family: Georgia, 'Iowan Old Style', serif; font-size: 22px; font-weight: 600; color: {{Ink}};">
                                        {{WebUtility.HtmlEncode(subject)}}
                                    </td>
                                </tr>
                                {{sectionsHtml}}
                                <tr>
                                    <td style="padding: 24px 32px 0 32px;">
                                        <div style="border-top: 1px solid {{Border}};"></div>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 16px 32px 32px 32px; font-size: 13px; line-height: 1.5; color: {{InkSoft}};">
                                        <a href="{{encodedUnsubscribeUrl}}" style="color: {{ClayDark}};">{{WebUtility.HtmlEncode(unsubscribeText)}}</a>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """;
    }

    // Fallbacks for IChannel's generic methods (see EmailChannel) — plain content, no digest
    // structure, since nothing in this codebase calls these today.
    public static string WrapPlainText(string text) => Wrap($"""<div style="white-space: pre-wrap;">{WebUtility.HtmlEncode(text)}</div>""");

    public static string WrapImageLink(string photoUrl, string? caption) => Wrap($"""
        {(caption is null ? "" : $"""<p>{WebUtility.HtmlEncode(caption)}</p>""")}
        <img src="{WebUtility.HtmlEncode(photoUrl)}" alt="" style="max-width: 100%;" />
        """);

    public static string WrapDocumentLink(string fileUrl, string fileName, string? caption) => Wrap($"""
        {(caption is null ? "" : $"""<p>{WebUtility.HtmlEncode(caption)}</p>""")}
        <a href="{WebUtility.HtmlEncode(fileUrl)}" style="color: {ClayDark};">{WebUtility.HtmlEncode(fileName)}</a>
        """);

    private static string Wrap(string bodyHtml) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8" /></head>
        <body style="margin: 0; padding: 24px; background-color: {Background}; font-family: 'Inter', -apple-system, 'Segoe UI', Arial, sans-serif; color: {Ink};">
            {bodyHtml}
        </body>
        </html>
        """;
}
