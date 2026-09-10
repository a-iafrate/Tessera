namespace Tessera.Web.Services;

public enum ToastKind
{
    Success,
    Error,
}

public sealed record ToastMessage(string Text, ToastKind Kind);

// Scoped (one per circuit, registered in Program.cs) — a page that just wrote something raises
// an event, and the single Toast host rendered once in MainLayout picks it up, regardless of
// which routed page is currently active. Fills the gap docs/12-stile-sito.md already named
// ("la comparsa dei toast di conferma") but never had an implementation for
// (docs/13-piano-miglioramenti.md, B11) — until now, a successful write was only visible as
// "the list quietly reloaded", and a failed one as an alert stuck at the top of the page, off
// screen on a long mobile page.
public sealed class ToastService
{
    public event Action<ToastMessage>? OnShow;

    public void ShowSuccess(string text) => OnShow?.Invoke(new ToastMessage(text, ToastKind.Success));

    public void ShowError(string text) => OnShow?.Invoke(new ToastMessage(text, ToastKind.Error));
}
