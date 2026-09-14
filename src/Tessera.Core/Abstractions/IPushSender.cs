namespace Tessera.Core.Abstractions;

// Deliberately plain strings, not a Tessera.Core.Users.PushSubscription — same shape as
// IEmailSender: the abstraction only needs to know how to deliver, not how the caller stores
// what it's delivering to.
public interface IPushSender
{
    Task SendAsync(string endpoint, string p256dh, string auth, string title, string body, string url, CancellationToken ct);
}

// Thrown instead of whatever HTTP-status-specific exception the underlying push library uses,
// so a caller (WebChannel) can react to "this subscription no longer exists" without depending
// on that library's types — the push service itself returns 404/410 once a subscription is
// uninstalled/expired/revoked, and the only correct response is to stop using it.
public sealed class PushSubscriptionGoneException(string endpoint) : Exception($"Push subscription '{endpoint}' is no longer valid.")
{
    public string Endpoint { get; } = endpoint;
}
