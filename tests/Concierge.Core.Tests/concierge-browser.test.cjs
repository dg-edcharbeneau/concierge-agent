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

test("light and dark transcript dates and timestamps meet AA text contrast", () => {
    const css = readFileSync(join(__dirname, "../../src/Concierge.Web/wwwroot/css/app.css"), "utf8");
    const luminance = hex => {
        const rgb = hex.match(/[a-f\d]{2}/gi).map(value => parseInt(value, 16) / 255)
            .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
    };
    const themes = css.matchAll(/:root(?:\[data-theme="dark"\])?\s*\{([^}]+)\}/g);
    let count = 0;
    for (const [, declarations] of themes) {
        const color = name => declarations.match(new RegExp(`--${name}:\\s*(#[a-f\\d]{6})`, "i"))[1];
        const foreground = luminance(color("text-muted"));
        const background = luminance(color("transcript-background"));
        const ratio = (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
        assert.ok(ratio >= 4.5, `Muted transcript text contrast ${ratio.toFixed(2)}:1 must be at least 4.5:1`);
        count++;
    }
    assert.equal(count, 2);
});
