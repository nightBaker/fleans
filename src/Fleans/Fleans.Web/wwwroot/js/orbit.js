// Small browser helpers for the Orbit shell and editor (css/orbit.css).
(function () {
    // Fluent writes body[data-theme] only once a mode is stored or changed. With Theme Mode = System and
    // nothing stored yet its tokens follow the OS while data-theme stays unset, so the --o-* tokens (and
    // the archify diagram style) would stay light on a dark OS. Fill it in the way Fluent itself does for
    // System, and follow OS switches for as long as the value is still ours.
    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    var ours = null;

    function syncSystemTheme() {
        var current = document.body.dataset.theme;
        if (current && current !== ours) return;
        ours = media && media.matches ? 'dark' : 'light';
        document.body.dataset.theme = ours;
    }

    syncSystemTheme();
    if (media && media.addEventListener) media.addEventListener('change', syncSystemTheme);
})();

window.fleansOrbit = {
    // Fluent writes the resolved theme ("dark", "light", "system-dark", ...) on <body data-theme>.
    isDark: function () {
        return (document.body.dataset.theme || '').endsWith('dark');
    },

    // Scrolls the first element matching the selector into view within its scroll container (the
    // instance debugger keeps its current step visible).
    scrollIntoView: function (selector) {
        var el = document.querySelector(selector);
        if (el) el.scrollIntoView({ block: 'nearest' });
    },

    // Clicks an element by id (e.g. a FluentInputFile anchor, so a second button can open its picker).
    click: function (id) {
        var el = document.getElementById(id);
        if (el) el.click();
    },

    // Returns false when the Clipboard API is unavailable or denied (non-secure origin, permissions).
    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    }
};
