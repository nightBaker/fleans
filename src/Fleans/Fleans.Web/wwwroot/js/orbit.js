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

// UI theme (Settings → UI Theme): 'orbit' (default) or 'bcchub' (css/bcchub.css, BCC HUB brand guide).
// Stored in localStorage and written to <html data-ui-theme> here, before the page paints, so the
// brand palette and fonts never flash. Fonts live on :root, hence <html> rather than <body>.
window.fleansUiTheme = (function () {
    var STORAGE_KEY = 'fleans.uiTheme';
    var THEMES = ['orbit', 'bcchub'];
    var DEFAULT_THEME = 'orbit';

    function apply(value) {
        document.documentElement.dataset.uiTheme = value;
    }

    function get() {
        try {
            var value = window.localStorage.getItem(STORAGE_KEY);
            return THEMES.indexOf(value) >= 0 ? value : DEFAULT_THEME;
        } catch (e) {
            return DEFAULT_THEME;
        }
    }

    apply(get());

    return {
        get: get,
        // Applies at once; returns false (theme still applied for this page) when storage is unavailable.
        set: function (value) {
            if (THEMES.indexOf(value) < 0) return false;
            apply(value);
            try {
                window.localStorage.setItem(STORAGE_KEY, value);
                return true;
            } catch (e) {
                return false;
            }
        }
    };
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
