import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

function createComposerHarness() {
    let keydown;
    runInNewContext(
        readFileSync(new URL("../src/Concierge.Web/wwwroot/js/concierge.js", import.meta.url), "utf8"),
        {
            document: {
                addEventListener(type, handler) {
                    if (type === "keydown") keydown = handler;
                },
                documentElement: { removeAttribute() {}, dataset: {} },
            },
            window: {},
            localStorage: { setItem() {}, getItem() { return null; } },
            sessionStorage: { setItem() {}, getItem() { return null; }, removeItem() {} },
            setTimeout,
            clearTimeout,
        });

    let submittedWith;
    const form = {
        requestSubmit(button) {
            submittedWith = button;
        },
        querySelector(selector) {
            return selector === 'button[type="submit"]' ? button : null;
        },
    };
    const button = { disabled: false };
    const textarea = {
        id: "message-draft",
        closest(selector) {
            return selector === "form" ? form : null;
        },
    };

    function press(overrides = {}) {
        let prevented = false;
        keydown({
            target: textarea,
            key: "Enter",
            shiftKey: false,
            isComposing: false,
            keyCode: 13,
            preventDefault() {
                prevented = true;
            },
            ...overrides,
        });
        return { prevented, submittedWith };
    }

    return { button, press, form };
}

test("Enter sends through the enabled submit button", () => {
    const harness = createComposerHarness();

    const result = harness.press();

    assert.equal(result.prevented, true);
    assert.equal(result.submittedWith, harness.button);
});

test("Shift+Enter leaves the newline in the textarea", () => {
    const harness = createComposerHarness();

    const result = harness.press({ shiftKey: true });

    assert.equal(result.prevented, false);
    assert.equal(result.submittedWith, undefined);
});

test("Enter during IME composition does not submit", () => {
    const harness = createComposerHarness();

    const composingResult = harness.press({ isComposing: true });
    const legacyCompositionResult = harness.press({ keyCode: 229 });

    assert.equal(composingResult.prevented, false);
    assert.equal(composingResult.submittedWith, undefined);
    assert.equal(legacyCompositionResult.prevented, false);
    assert.equal(legacyCompositionResult.submittedWith, undefined);
});

test("Enter does not submit when the send button is disabled", () => {
    const harness = createComposerHarness();
    harness.button.disabled = true;

    const result = harness.press();

    assert.equal(result.prevented, false);
    assert.equal(result.submittedWith, undefined);
});
