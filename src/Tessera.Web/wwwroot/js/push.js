window.tesseraPush = {
    // PushManager wants the VAPID public key as a Uint8Array, not the base64url string the
    // server hands over (docs/13-piano-miglioramenti.md, C2) — standard conversion, no library
    // needed for something this small.
    urlBase64ToUint8Array: function (base64String) {
        var padding = '='.repeat((4 - (base64String.length % 4)) % 4);
        var base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        var rawData = window.atob(base64);
        var outputArray = new Uint8Array(rawData.length);
        for (var i = 0; i < rawData.length; ++i) {
            outputArray[i] = rawData.charCodeAt(i);
        }
        return outputArray;
    },

    isSupported: function () {
        return 'serviceWorker' in navigator && 'PushManager' in window;
    },

    // Returns 'subscribed' | 'unsubscribed' | 'denied' | 'unsupported' — Settings.razor uses
    // this both on load (to show the right button) and after subscribe/unsubscribe.
    getStatus: async function () {
        if (!this.isSupported()) {
            return 'unsupported';
        }
        if (Notification.permission === 'denied') {
            return 'denied';
        }
        var registration = await navigator.serviceWorker.ready;
        var existing = await registration.pushManager.getSubscription();
        return existing ? 'subscribed' : 'unsubscribed';
    },

    subscribe: async function (vapidPublicKey) {
        if (!this.isSupported()) {
            return 'unsupported';
        }

        var permission = await Notification.requestPermission();
        if (permission !== 'granted') {
            return 'denied';
        }

        var registration = await navigator.serviceWorker.ready;
        var subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: this.urlBase64ToUint8Array(vapidPublicKey)
        });

        var json = subscription.toJSON();
        await fetch('/push/subscribe', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth })
        });

        return 'subscribed';
    },

    unsubscribe: async function () {
        if (!this.isSupported()) {
            return 'unsupported';
        }

        var registration = await navigator.serviceWorker.ready;
        var subscription = await registration.pushManager.getSubscription();
        if (!subscription) {
            return 'unsubscribed';
        }

        var endpoint = subscription.endpoint;
        await subscription.unsubscribe();
        await fetch('/push/unsubscribe', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ endpoint: endpoint })
        });

        return 'unsubscribed';
    }
};
