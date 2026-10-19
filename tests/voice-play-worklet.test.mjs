import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

function worklet() {
    let Processor;
    const messages = [];
    runInNewContext(readFileSync(new URL("../src/Concierge.Web/wwwroot/js/voice-play-worklet.js", import.meta.url), "utf8"), {
        AudioWorkletProcessor: class {
            port = { postMessage: message => messages.push(message) };
        },
        registerProcessor: (_name, processor) => { Processor = processor; },
    });
    const processor = new Processor();
    return {
        messages,
        send: data => processor.port.onmessage({ data }),
        render() {
            const output = new Float32Array(128);
            processor.process([], [[output]]);
            return output;
        },
    };
}

for (const scenario of ["idle", "queued-only", "silent-pcm", "drained", "playing", "last-block"]) {
    test(`silence acknowledgment classifies ${scenario} using rendered audio`, () => {
        const w = worklet();
        if (scenario !== "idle") {
            const length = scenario === "playing" ? 960 : 128;
            w.send({
                type: "audio",
                samples: new Float32Array(length).fill(scenario === "silent-pcm" ? 0 : 0.5),
                playbackToken: "reply-7",
            });
            if (scenario !== "queued-only") w.render();
            if (scenario === "drained") w.render();
        }
        w.send({ type: "flush", interruptionNumber: 7 });
        assert.equal(w.messages.filter(m => m.type === "silenced").length, 0);
        // New answer audio must not prevent a completely silent acknowledgment block.
        w.send({ type: "audio", samples: new Float32Array(480).fill(0.25) });
        assert.ok(w.render().every(sample => sample === 0));
        const ack = w.messages.find(m => m.type === "silenced");
        assert.equal(ack.interruptionNumber, 7);
        assert.equal(ack.bargeIn, scenario === "playing" || scenario === "last-block");
        assert.equal(ack.playbackToken, scenario === "playing" || scenario === "last-block" ? "reply-7" : null);
        assert.ok(w.render().some(sample => sample !== 0));
    });
}

test("silence acknowledgment returns the token of the last rendered audio segment", () => {
    const w = worklet();
    w.send({ type: "audio", samples: new Float32Array(128).fill(0.5), playbackToken: "older-reply" });
    w.render();
    w.send({ type: "audio", samples: new Float32Array(128).fill(0.25), playbackToken: "current-reply" });
    w.render();
    w.send({ type: "flush", interruptionNumber: 9 });
    w.render();
    const ack = w.messages.find(m => m.type === "silenced");
    assert.equal(ack.bargeIn, true);
    assert.equal(ack.playbackToken, "current-reply");
});

test("duplicate flush cannot create another barge-in from discarded playback", () => {
    const w = worklet();
    w.send({ type: "audio", samples: new Float32Array(960).fill(0.5), playbackToken: "reply-1" });
    w.render();
    w.send({ type: "flush", interruptionNumber: 1 });
    w.send({ type: "flush", interruptionNumber: 1 });
    w.render();
    const acks = w.messages.filter(m => m.type === "silenced");
    assert.equal(acks[0].bargeIn, true);
    assert.equal(acks[0].playbackToken, "reply-1");
    assert.equal(acks[1].bargeIn, false);
    assert.equal(acks[1].playbackToken, null);
    assert.equal(w.messages.findLast(m => m.type === "played").samples, 960);
});
