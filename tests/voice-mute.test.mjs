import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

function createVoiceHarness() {
    const sockets = [];
    const audioNodes = [];
    const tracks = [{ enabled: true, stop() { this.stopped = true; } }];

    class FakeAudioContext {
        audioWorklet = { addModule: async () => {} };
        destination = {};
        async resume() {}
        async suspend() {}
        async close() {}
        createMediaStreamSource() {
            return { connect() {} };
        }
    }

    class FakeAudioWorkletNode {
        port = {
            messages: [],
            postMessage(message) { this.messages.push(message); },
            onmessage: null,
        };

        constructor(_context, name) {
            this.name = name;
            audioNodes.push(this);
        }

        connect() {}
    }

    class FakeWebSocket {
        static CONNECTING = 0;
        static OPEN = 1;
        static CLOSED = 3;
        readyState = FakeWebSocket.OPEN;
        bufferedAmount = 0;
        sent = [];

        constructor() {
            sockets.push(this);
        }

        send(data) { this.sent.push(data); }
        close() { this.readyState = FakeWebSocket.CLOSED; }
    }

    const document = {
        visibilityState: "visible",
        addEventListener() {},
    };
    const window = { addEventListener() {} };
    runInNewContext(
        readFileSync(new URL("../src/Concierge.Web/wwwroot/js/voice.js", import.meta.url), "utf8"),
        {
            AudioContext: FakeAudioContext,
            AudioWorkletNode: FakeAudioWorkletNode,
            WebSocket: FakeWebSocket,
            document,
            window,
            location: { protocol: "https:", host: "hotel.test" },
            navigator: {
                mediaDevices: {
                    getUserMedia: async () => ({ getTracks: () => tracks }),
                },
            },
            URL,
            encodeURIComponent,
        });

    return { voice: window.conciergeVoice, sockets, audioNodes, tracks, document };
}

const flushMicrotasks = () => new Promise(resolve => setImmediate(resolve));

test("mute suppresses microphone frames while keeping the relay and playback active", async () => {
    const harness = createVoiceHarness();
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    const socket = harness.sockets[0];
    socket.onmessage({ data: JSON.stringify({ type: "SettingsApplied" }) });
    await flushMicrotasks();

    const microphone = harness.audioNodes.find(node => node.name === "concierge-mic");
    const playback = harness.audioNodes.find(node => node.name === "concierge-play");
    assert.ok(microphone);
    assert.ok(playback);

    const microphoneFrame = new ArrayBuffer(640);
    harness.voice.setMuted(true);
    assert.equal(harness.tracks[0].enabled, false);
    microphone.port.onmessage({ data: microphoneFrame });
    assert.deepEqual(socket.sent, []);
    assert.equal(socket.readyState, 1);

    socket.onmessage({ data: new ArrayBuffer(2) });
    assert.equal(playback.port.messages[0].type, "audio");
    playback.port.onmessage({ data: { type: "played", samples: 240 } });
    assert.deepEqual(socket.sent, ['{"type":"PlaybackAck","samples":240}']);

    harness.voice.setMuted(false);
    assert.equal(harness.tracks[0].enabled, true);
    microphone.port.onmessage({ data: microphoneFrame });
    assert.equal(socket.sent.at(-1), microphoneFrame);
    assert.equal(socket.readyState, 1);
});

test("mute set before microphone setup keeps the newly acquired track disabled", async () => {
    const harness = createVoiceHarness();
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    harness.voice.setMuted(true);

    const socket = harness.sockets[0];
    socket.onmessage({ data: JSON.stringify({ type: "SettingsApplied" }) });
    await flushMicrotasks();

    assert.equal(harness.tracks[0].enabled, false);
    const microphone = harness.audioNodes.find(node => node.name === "concierge-mic");
    microphone.port.onmessage({ data: new ArrayBuffer(640) });
    assert.deepEqual(socket.sent, []);
    assert.equal(socket.readyState, 1);
});
