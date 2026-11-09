const { test } = require("node:test");
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const { runInNewContext } = require("node:vm");

function harness() {
    const timers = new Map();
    let nextTimer = 0;
    const window = {};
    runInNewContext(
        readFileSync(join(__dirname, "../../src/Concierge.Web/wwwroot/js/concierge.js"), "utf8"),
        {
            window,
            // concierge.js registers its Enter-to-send listener at load; these tests don't exercise it.
            document: { addEventListener() { } },
            setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; },
            clearTimeout: id => timers.delete(id)
        });
    const element = () => ({
        scrollHeight: 300, scrollTop: 0, clientHeight: 100, listeners: new Map(),
        addEventListener(name, handler) { this.listeners.set(name, handler); },
        removeEventListener(name, handler) {
            if (this.listeners.get(name) === handler) this.listeners.delete(name);
        }
    });
    return { chat: window.conciergeChat, timers, element };
}

test("cancel wires the replacement transcript and removes the old listener and timer", () => {
    const { chat, timers, element } = harness();
    const old = element();
    const replacement = element();
    const calls = [];
    const reference = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
    chat.wireTranscript(old, reference, 4);
    old.listeners.get("scroll")();
    assert.equal(timers.size, 1);
    chat.unwireTranscript();
    assert.equal(timers.size, 0);
    assert.equal(old.listeners.size, 0);
    chat.wireTranscript(replacement, reference, 4);
    replacement.listeners.get("scroll")();
    for (const callback of timers.values()) callback();
    assert.deepEqual(calls, [["OnTranscriptScroll", false, 4]]);
});

test("rewiring and ending cancel queued callbacks and release listeners", () => {
    const { chat, timers, element } = harness();
    const old = element();
    const current = element();
    const reference = { invokeMethodAsync: () => { throw new Error("Stale callback"); } };
    chat.wireTranscript(old, reference, 1);
    old.listeners.get("scroll")();
    chat.wireTranscript(current, reference, 2);
    assert.equal(timers.size, 0);
    assert.equal(old.listeners.size, 0);
    current.listeners.get("scroll")();
    chat.unwireTranscript();
    chat.unwireTranscript();
    assert.equal(timers.size, 0);
    assert.equal(current.listeners.size, 0);
});

test("every brand's light and dark tokens meet AA text contrast", () => {
    const css = readFileSync(join(__dirname, "../../src/Concierge.Web/wwwroot/css/app.css"), "utf8");
    const luminance = hex => {
        const rgb = hex.match(/[a-f\d]{2}/gi).map(value => parseInt(value, 16) / 255)
            .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
    };
    const contrast = (a, b) => {
        const [x, y] = [luminance(a), luminance(b)];
        return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
    };
    const pairs = [
        ["text-muted", "transcript-background"],
        ["text-primary", "surface-background"],
        ["text-secondary", "surface-background"],
        ["text-primary", "page-background"],
        ["suggestion-text", "suggestion-background"],
        ["guest-message-text", "guest-message-background"],
        ["error-text", "error-background"],
        ["primary-action-text", "primary-action-background"],
        ["primary-action-text", "primary-action-hover"]
    ];
    // Brand blocks are the ones that set --page-background: bare :root (Temporal) and each [data-brand].
    const brands = [...css.matchAll(/(:root(?:\[data-brand="(\w+)"\])?)\s*\{([^}]*--page-background[^}]*)\}/g)];
    const names = brands.map(([, , brand]) => brand ?? "temporal");
    assert.deepEqual(names, ["temporal", "deepgram"]);
    for (const [, , brand = "temporal", declarations] of brands) {
        const color = (name, mode) => {
            const match = declarations.match(new RegExp(
                `--${name}:\\s*light-dark\\(\\s*(#[a-f\\d]{6})\\s*,\\s*(#[a-f\\d]{6})\\s*\\)`, "i"));
            assert.ok(match, `${brand} must set --${name} as light-dark(#rrggbb, #rrggbb)`);
            return match[mode === "light" ? 1 : 2];
        };
        for (const mode of ["light", "dark"]) {
            for (const [foreground, background] of pairs) {
                const ratio = contrast(color(foreground, mode), color(background, mode));
                assert.ok(ratio >= 4.5,
                    `${brand} ${mode}: --${foreground} on --${background} is ${ratio.toFixed(2)}:1, needs 4.5:1`);
            }
        }
    }
});

function themeHarness({ stored = null, storageThrows = false } = {}) {
    const attributes = new Map();
    const storage = new Map(stored === null ? [] : [["hotel-zgy-theme", stored]]);
    const guard = () => { if (storageThrows) throw new Error("SecurityError"); };
    const root = {
        dataset: new Proxy({}, {
            set: (_, key, value) => { attributes.set(`data-${key}`, String(value)); return true; },
            get: (_, key) => attributes.get(`data-${String(key)}`)
        }),
        removeAttribute: name => attributes.delete(name)
    };
    const window = {};
    runInNewContext(
        readFileSync(join(__dirname, "../../src/Concierge.Web/wwwroot/js/theme.js"), "utf8"),
        {
            window,
            document: { documentElement: root },
            localStorage: {
                getItem: key => { guard(); return storage.get(key) ?? null; },
                setItem: (key, value) => { guard(); storage.set(key, value); }
            }
        });
    return { theme: window.conciergeTheme, attributes, storage };
}

test("theme values normalize, and pre-brand values map to Temporal", () => {
    const { theme } = themeHarness();
    assert.equal(theme.normalize("dark"), "temporal-dark");
    assert.equal(theme.normalize("light"), "temporal-light");
    assert.equal(theme.normalize("system"), "temporal-system");
    assert.equal(theme.normalize("deepgram-dark"), "deepgram-dark");
    assert.equal(theme.normalize("deepgram-dark-extra"), "temporal-system");
    assert.equal(theme.normalize("acme-dark"), "temporal-system");
    assert.equal(theme.normalize(null), "temporal-system");
});

test("the stored theme is applied as soon as the script runs", () => {
    const { attributes, storage } = themeHarness({ stored: "dark" });
    assert.equal(attributes.get("data-brand"), "temporal");
    assert.equal(attributes.get("data-theme"), "dark");
    assert.equal(storage.get("hotel-zgy-theme"), "temporal-dark");
});

test("system mode keeps the brand and removes the explicit mode", () => {
    const { theme, attributes, storage } = themeHarness({ stored: "deepgram-light" });
    assert.equal(attributes.get("data-theme"), "light");
    assert.equal(theme.apply("deepgram-system"), "deepgram-system");
    assert.equal(attributes.get("data-brand"), "deepgram");
    assert.equal(attributes.has("data-theme"), false);
    assert.equal(storage.get("hotel-zgy-theme"), "deepgram-system");
});

test("blocked storage still applies the theme", () => {
    const { theme, attributes } = themeHarness({ storageThrows: true });
    assert.equal(attributes.get("data-brand"), "temporal");
    assert.equal(attributes.has("data-theme"), false);
    assert.equal(theme.apply("deepgram-dark"), "deepgram-dark");
    assert.equal(attributes.get("data-brand"), "deepgram");
    assert.equal(attributes.get("data-theme"), "dark");
});
