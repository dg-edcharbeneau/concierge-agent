// Run after building Concierge.Web:
// PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs node --test tests/glass-contrast.test.mjs
// Without PLAYWRIGHT_MODULE, uses an already installed "playwright" package.
//
// Text sits directly on the tinted glass over the hotel photo, so its contrast depends on the
// rendered pixels, not just the tokens. For every brand and mode this renders the real app.css and
// photo, hides everything inside each glass container, screenshots the bare glass, and checks every
// text color used on that glass against the darkest and lightest pixel anywhere in the container.
// Checking the whole container covers text at any scroll position. Keep the markup aligned with
// Home.razor, FocusCard.razor and RideActivityPanel.razor.
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href
    : "playwright");
const wwwroot = new URL("../src/Concierge.Web/wwwroot/", import.meta.url);
const scopedCss = readFileSync(new URL(
    "../src/Concierge.Web/obj/Debug/net11.0/scopedcss/bundle/Concierge.Web.styles.css",
    import.meta.url), "utf8");
const scope = scopedCss.match(/\.ride-activity-drawer\[(b-[\w]+)\]/)?.[1];
assert.ok(scope, "Build Concierge.Web first to generate the actual drawer scoped CSS");

const ORIGIN = "https://concierge.test";
const THEMES = [["temporal", "light"], ["temporal", "dark"], ["deepgram", "light"], ["deepgram", "dark"]];
// The largest screens show the most photo behind the panels and set the limits; the phone covers
// the focus card, the only glass panel on the photo at that width besides the header.
const DESKTOP = [[1440, 900], [2560, 1440], [3840, 2160]];
const PHONE = [[390, 844]];
const CONTAINERS = [".site-header", ".focus-card", ".chat-panel", ".ride-activity-drawer"];
const TOKENS = ["text-primary", "text-secondary", "text-muted"];

const header = `
    <header class="site-header voice-site-header">
        <a class="hotel-brand" href="/"><strong>Hotel ZGY</strong><span>/ Miami</span></a>
        <div class="header-actions voice-header-actions">
            <div class="voice-header-controls has-voice-mode" role="group" aria-label="Chat controls">
                <button class="end-chat-button voice-end-chat-button" type="button"><span>End chat</span></button>
            </div>
        </div>
    </header>`;

const verify = `
    <main class="main-content focus-main">
        <section class="focus-card">
            <h1>Verify your stay</h1>
            <p class="focus-card-intro">Enter your last name and room number to continue.</p>
            <form class="verification-form">
                <label for="last-name">Last name</label><input id="last-name" type="text" placeholder="Last name">
                <label for="room-number">Room number</label><input id="room-number" type="text" placeholder="Room number">
                <p class="field-hint">Enter the room number exactly as provided.</p>
                <button class="primary-button focus-card-action" type="button">Verify and continue</button>
            </form>
        </section>
    </main>`;

const message = (guest, text) => `
    <div class="message-row ${guest ? "guest-row" : "concierge-row"}">
        <div class="message-content">
            <div class="message-meta"><strong>${guest ? "GUEST" : "CONCIERGE"}</strong><time>9:13 PM</time></div>
            <div class="message-bubble ${guest ? "guest-bubble" : "concierge-bubble"}">${text}</div>
        </div>
    </div>`;

const drawer = `
    <aside class="ride-activity-drawer is-open" aria-labelledby="ride-activity-title">
        <header class="ride-activity-header">
            <div><h2 id="ride-activity-title">Activity</h2>
                <p class="ride-activity-subtitle">Updates for your stay · Miami time (ET)</p></div>
        </header>
        <div class="ride-activity-content">
            <div class="ride-activity-empty" role="status"><h3>No activity yet</h3>
                <p>Ride updates will appear here when you book a ride with the concierge.</p></div>
        </div>
        <footer class="ride-activity-footer"><p>To change or cancel a ride, ask the concierge.</p></footer>
    </aside>`.replace(/<([a-z][\w-]*)(?=[\s>])/g, `<$1 ${scope}`);

const chat = `
    <main class="main-content activity-main-content">
        <div class="activity-layout activity-layout-open">
            <section class="chat-panel" aria-label="Hotel concierge chat">
                <button class="questions-heading" type="button" aria-expanded="true"><span>How can we help?</span></button>
                <section class="suggestions" aria-label="Suggested questions">
                    <label for="suggestion-select">Suggested question</label>
                    <select id="suggestion-select"><option>Choose a suggested question…</option></select>
                    <div class="suggestion-footer"><p>Choose a question, then send it.</p>
                        <button class="suggestion-send" type="button">Send suggestion</button></div>
                </section>
                <div class="conversation-heading"><span>YOUR STAY CONVERSATION</span></div>
                <div class="transcript-wrap"><div class="transcript">
                    <div class="date-separator"><span>Nov 8</span></div>
                    ${message(true, "What are the pool hours?")}
                    ${message(false, "The Hotel ZGY pool is open from 8:00 a.m. to 8:00 p.m.")}
                </div></div>
                <form class="composer">
                    <label class="message-label" for="message-draft">MESSAGE</label>
                    <div class="compose-box"><textarea id="message-draft" rows="3"></textarea>
                        <div class="compose-footer"><span class="composer-hint">Enter to send</span>
                            <button class="primary-button send-button" type="button">Send</button></div></div>
                </form>
            </section>${drawer}
        </div>
    </main>`;

const fixture = (brand, mode, body) => `<!doctype html>
    <html lang="en" data-brand="${brand}" data-theme="${mode}"><head><meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <link rel="stylesheet" href="/css/app.css"><style>${scopedCss}</style></head>
    <body><div class="page-shell">${header}${body}</div></body></html>`;

// Text with no painted layer between it and its glass container, plus every text token as it
// resolves on that container (timestamps, hints and labels can appear anywhere on the glass).
function textOnGlass({ containers, tokens }) {
    const out = {};
    for (const sel of containers) {
        const box = document.querySelector(sel);
        if (!box) continue;
        const rect = box.getBoundingClientRect();
        const style = getComputedStyle(box);
        if (rect.width < 40 || rect.height < 30 || style.visibility === "hidden" || +style.opacity === 0) continue;
        const glassy = style.backdropFilter.includes("blur");
        const texts = [];
        const walker = document.createTreeWalker(box, NodeFilter.SHOW_TEXT);
        for (let node; (node = walker.nextNode());) {
            if (!node.textContent.trim()) continue;
            const el = node.parentElement;
            if (el.closest("option, select, textarea, input, button, .message-bubble")) continue;
            let painted = false;
            for (let a = el; a && a !== box; a = a.parentElement) {
                const s = getComputedStyle(a);
                if (s.backgroundImage !== "none" || !/rgba\(0, 0, 0, 0\)|transparent/.test(s.backgroundColor)) { painted = true; break; }
            }
            if (painted) continue;
            const cs = getComputedStyle(el);
            const size = parseFloat(cs.fontSize);
            texts.push({ text: node.textContent.trim().slice(0, 30), color: cs.color,
                large: size >= 24 || (size >= 18.66 && +cs.fontWeight >= 700) });
        }
        const probe = document.createElement("span");
        box.append(probe);
        for (const token of [...tokens, ...(sel === ".chat-panel" ? ["glass-label"] : [])]) {
            probe.style.color = `var(--${token})`;
            texts.push({ text: `--${token}`, color: getComputedStyle(probe).color, large: false });
        }
        probe.remove();
        out[sel] = { glassy, rect: { x: rect.x, y: rect.y, w: rect.width, h: rect.height }, texts };
    }
    return out;
}

// Darkest and lightest relative luminance in each container, 16px in from the edge highlight.
async function luminanceRange(canvasPage, png, rects) {
    return canvasPage.evaluate(async ({ b64, rects }) => {
        const img = new Image();
        img.src = "data:image/png;base64," + b64;
        await img.decode();
        const canvas = document.createElement("canvas");
        canvas.width = img.width;
        canvas.height = img.height;
        const ctx = canvas.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(img, 0, 0);
        const lin = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
        const out = {};
        for (const [sel, r] of Object.entries(rects)) {
            const inset = 16;
            const data = ctx.getImageData(Math.round(r.x + inset), Math.round(r.y + inset),
                Math.round(r.w - 2 * inset), Math.round(r.h - 2 * inset)).data;
            let min = 1, max = 0;
            for (let i = 0; i < data.length; i += 4) {
                const l = 0.2126 * lin(data[i]) + 0.7152 * lin(data[i + 1]) + 0.0722 * lin(data[i + 2]);
                if (l < min) min = l;
                if (l > max) max = l;
            }
            out[sel] = { min, max };
        }
        return out;
    }, { b64: png.toString("base64"), rects });
}

// Computed colors arrive as rgb(0-255) or, after color-mix(), color(srgb 0-1).
function luminance(css) {
    const nums = (css.match(/-?[\d.]+/g) || []).map(Number).slice(0, 3);
    const unit = css.startsWith("color(") ? nums : nums.map(v => v / 255);
    const [r, g, b] = unit.map(v => v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

// The weakest contrast over a background luminance range; 1 when the text's own luminance is inside it.
function worstContrast(color, { min, max }) {
    const f = luminance(color);
    if (f >= min && f <= max) return 1;
    const ratio = b => (Math.max(f, b) + 0.05) / (Math.min(f, b) + 0.05);
    return Math.min(ratio(min), ratio(max));
}

test("text on the tinted glass meets AA over the hotel photo in every brand and mode", async t => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext();
        await context.route(`${ORIGIN}/**`, route => {
            const path = new URL(route.request().url()).pathname;
            if (path === "/css/app.css") return route.fulfill({ path: new URL("css/app.css", wwwroot).pathname, contentType: "text/css" });
            if (path.startsWith("/images/")) return route.fulfill({ path: new URL(path.slice(1), wwwroot).pathname, contentType: "image/jpeg" });
            return route.fulfill({ status: 404 });
        });
        const page = await context.newPage();
        const canvasPage = await context.newPage();
        await canvasPage.setContent("<!doctype html><title>pixels</title>");
        const failures = [];
        let measured = 0;

        for (const [brand, mode] of THEMES) {
            for (const [state, body, viewports] of [["verify", verify, [...DESKTOP, ...PHONE]], ["chat", chat, DESKTOP]]) {
                for (const [width, height] of viewports) {
                    await page.setViewportSize({ width, height });
                    await page.route(`${ORIGIN}/`, route => route.fulfill({ body: fixture(brand, mode, body), contentType: "text/html" }));
                    await page.goto(`${ORIGIN}/`, { waitUntil: "networkidle" });
                    await page.unroute(`${ORIGIN}/`);
                    // The photo must actually load; a missing image would make the glass look safer than it is.
                    const photoLoaded = await page.evaluate(async () => {
                        const url = getComputedStyle(document.querySelector(".page-shell")).backgroundImage.match(/url\("([^"]+)"\)/)?.[1];
                        if (!url) return false;
                        const img = new Image();
                        img.src = url;
                        return img.decode().then(() => img.naturalWidth > 0, () => false);
                    });
                    assert.ok(photoLoaded, "The page shell must paint the hotel photo");

                    const boxes = await page.evaluate(textOnGlass, { containers: CONTAINERS, tokens: TOKENS });
                    for (const [sel, box] of Object.entries(boxes)) {
                        assert.ok(box.glassy, `${brand} ${mode} ${state} ${width}x${height}: ${sel} must have a backdrop blur`);
                    }

                    await page.addStyleTag({ content: `${CONTAINERS.map(s => `${s} *`).join(", ")} { visibility: hidden !important; }` });
                    const png = await page.screenshot();
                    const ranges = await luminanceRange(canvasPage,
                        png, Object.fromEntries(Object.entries(boxes).map(([sel, box]) => [sel, box.rect])));

                    for (const [sel, box] of Object.entries(boxes)) {
                        for (const text of box.texts) {
                            const ratio = worstContrast(text.color, ranges[sel]);
                            const needed = text.large ? 3 : 4.5;
                            measured++;
                            if (ratio < needed) {
                                failures.push(`${brand} ${mode} ${state} ${width}x${height} ${sel} "${text.text}": ` +
                                    `${ratio.toFixed(2)}:1, needs ${needed}:1`);
                            }
                        }
                    }
                }
            }
        }

        t.diagnostic(`${measured} text checks`);
        assert.ok(measured > 400, `Expected the fixture to expose text on every glass container, measured ${measured}`);
        assert.deepEqual(failures, []);
    } finally {
        await browser.close();
    }
});
