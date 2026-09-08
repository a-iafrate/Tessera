window.tesseraTheme = {
    // "auto" removes the override entirely, letting the [data-theme]-less :root rules and the
    // prefers-color-scheme media query in tokens.css decide (docs/12-stile-sito.md:
    // "dark mode segue prefers-color-scheme di default, con toggle manuale che sovrascrive").
    set: function (theme) {
        if (theme === 'light' || theme === 'dark') {
            localStorage.setItem('tessera-theme', theme);
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            localStorage.removeItem('tessera-theme');
            document.documentElement.removeAttribute('data-theme');
        }
    },

    get: function () {
        var stored = localStorage.getItem('tessera-theme');
        return (stored === 'light' || stored === 'dark') ? stored : 'auto';
    }
};
