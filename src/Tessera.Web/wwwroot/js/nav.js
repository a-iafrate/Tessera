window.tesseraNav = {
    handlers: {},

    // Registered once per circuit (MainLayout.OnAfterRenderAsync, firstRender) rather than
    // toggled open/close with the menu itself — the handler checks the DOM's own nav-open
    // class before ever calling back into .NET, so there's nothing to keep in sync across
    // Blazor re-renders (docs/13-piano-miglioramenti.md, B6).
    init: function (dotNetRef, containerId) {
        if (this.handlers[containerId]) {
            return;
        }

        var handler = function (e) {
            var container = document.getElementById(containerId);
            var nav = container && container.querySelector('nav');
            if (nav && nav.classList.contains('nav-open') && !container.contains(e.target)) {
                dotNetRef.invokeMethodAsync('CloseMenuFromOutsideClick');
            }
        };

        document.addEventListener('click', handler, true);
        this.handlers[containerId] = handler;
    },

    dispose: function (containerId) {
        var handler = this.handlers[containerId];
        if (handler) {
            document.removeEventListener('click', handler, true);
            delete this.handlers[containerId];
        }
    }
};
