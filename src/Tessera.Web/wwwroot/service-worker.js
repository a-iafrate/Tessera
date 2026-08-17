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
