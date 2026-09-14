// Tessera is a Blazor Server app: every interaction needs a live connection to the backend,
// so there is no meaningful offline mode and this worker deliberately caches nothing. It
// exists only to satisfy the browser's installability requirement (manifest + a registered
// fetch handler) for "Add to Home Screen" — every request still falls through to the network.
self.addEventListener('install', function (event) {
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', function () {
    // Intentional no-op — no respondWith() means the browser's default network fetch runs.
});

// Web push (docs/13-piano-miglioramenti.md, C2) — the payload shape is set by
// WebPushSender/EmailChannel's sibling on the server (Tessera.Integrations/WebPushSender.cs):
// { title, body, url }. This is the one thing this worker does beyond satisfying the
// installability requirement above.
self.addEventListener('push', function (event) {
    var data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch (e) {
        // Not JSON for some reason — still show *something* rather than silently drop it.
    }

    event.waitUntil(self.registration.showNotification(data.title || 'Tessera', {
        body: data.body || '',
        icon: 'images/tessera-mark.svg',
        data: { url: data.url || '/chat' }
    }));
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    event.waitUntil(self.clients.openWindow(event.notification.data && event.notification.data.url || '/chat'));
});
