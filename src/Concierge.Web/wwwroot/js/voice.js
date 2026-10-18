// Browser side of the voice relay: microphone capture, paced agent playback with acknowledgements,
// and barge-in flushes. Ported from the relay-latency prototype. The page never starts the
// microphone on its own; every start is a guest click.
(() => {
    const protocol = "concierge-voice";
    const maxMicBacklogBytes = 64 * 1024;
    const closeMessages = {
        4000: "Voice disconnected.",
        4401: "Your chat ended. Verify your stay again to continue.",
        4503: "Voice is unavailable right now. Use text chat or try again later.",
    };

    let current = null;

    function notify(voice, state, message = null, code = 0) {
        if (voice.state === state && state !== "disconnected") return;
        voice.state = state;
        voice.dotNet.invokeMethodAsync("OnVoiceState", state, message, code).catch(() => {});
    }

    async function createPlayback(voice) {
        const context = new AudioContext({ sampleRate: 24000, latencyHint: "interactive" });
        voice.playContext = context;
        await context.audioWorklet.addModule("/js/voice-play-worklet.js");
        const node = new AudioWorkletNode(context, "concierge-play", { outputChannelCount: [1] });
        node.connect(context.destination);
        node.port.onmessage = (event) => onPlayback(voice, event.data);
        voice.playNode = node;
        await context.resume();
    }

    async function startMicrophone(voice) {
        const context = new AudioContext({ latencyHint: "interactive" });
        voice.micContext = context;
        await context.audioWorklet.addModule("/js/voice-mic-worklet.js");
        voice.micStream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        });
        if (voice.stopped) {
            voice.micStream.getTracks().forEach((track) => track.stop());
            return;
        }

        const node = new AudioWorkletNode(context, "concierge-mic");
        node.port.onmessage = (event) => {
            const socket = voice.socket;
            if (!socket || socket.readyState !== WebSocket.OPEN) return;
            // Never drop speech silently: a stalled uplink ends voice so the guest can reconnect.
            if (socket.bufferedAmount > maxMicBacklogBytes) {
                end(voice, "disconnected", closeMessages[4000], 4000);
                return;
            }
            socket.send(event.data);
        };
        context.createMediaStreamSource(voice.micStream).connect(node);
        await context.resume();
        notify(voice, "listening");
    }

    function onPlayback(voice, message) {
        switch (message.type) {
            case "played":
                if (voice.socket?.readyState === WebSocket.OPEN) {
                    voice.socket.send(JSON.stringify({ type: "PlaybackAck", samples: message.samples }));
                }
                break;
            case "speaking":
                notify(voice, "speaking");
                break;
            case "silenced":
                if (voice.socket?.readyState === WebSocket.OPEN) {
                    voice.socket.send(JSON.stringify({
                        type: "InterruptionSilenced",
                        interruptionNumber: message.interruptionNumber,
                        bargeIn: message.bargeIn,
                    }));
                }
                break;
            case "drained":
                voice.playbackActive = false;
                if (voice.endAfterPlayback) requestEndChat(voice);
                if (voice.state === "speaking") notify(voice, "listening");
                break;
            case "overflow":
                end(voice, "disconnected", closeMessages[4000], 4000);
                break;
        }
    }

    function onSocketMessage(voice, event) {
        if (typeof event.data !== "string") {
            voice.playbackActive = true;
            const pcm = new Int16Array(event.data);
            const samples = new Float32Array(pcm.length);
            for (let i = 0; i < pcm.length; i++) samples[i] = pcm[i] / 0x8000;
            voice.playNode.port.postMessage({ type: "audio", samples }, [samples.buffer]);
            return;
        }

        const message = JSON.parse(event.data);
        switch (message.type) {
            case "VoiceSession":
                voice.dotNet.invokeMethodAsync("OnVoiceSession", message.voiceSessionId).catch(() => {});
                break;
            case "SettingsApplied":
                startMicrophone(voice).catch((error) => {
                    const denied = error && (error.name === "NotAllowedError" || error.name === "SecurityError");
                    end(voice, "disconnected", denied
                        ? "Microphone access is blocked. Allow it in the browser's site settings, then reconnect."
                        : "The microphone couldn't start. Check your input device, then reconnect.", 0);
                });
                break;
            case "UserStartedSpeaking":
                voice.endAfterPlayback = false;
                voice.endRequested = false;
                voice.playNode.port.postMessage({
                    type: "flush",
                    interruptionNumber: message.interruptionNumber,
                });
                notify(voice, "listening");
                break;
            case "ConversationText":
                if (message.role === "user") notify(voice, "thinking");
                break;
            case "AgentThinking":
                notify(voice, "thinking");
                break;
            case "EndChatRequested":
                requestEndChat(voice);
                break;
            case "EndChatAfterPlayback":
                voice.endAfterPlayback = true;
                if (!voice.playbackActive) requestEndChat(voice);
                break;
            case "CancelEndChat":
                voice.endAfterPlayback = false;
                voice.endRequested = false;
                break;
        }
    }

    function requestEndChat(voice) {
        if (voice.endRequested || voice.stopped) return;
        voice.endRequested = true;
        voice.dotNet.invokeMethodAsync("OnVoiceEndRequested").catch(() => {});
    }

    function release(voice) {
        voice.micStream?.getTracks().forEach((track) => track.stop());
        voice.micContext?.close().catch(() => {});
        voice.playContext?.close().catch(() => {});
    }

    function end(voice, state, message, code) {
        if (voice.stopped) return;
        voice.stopped = true;
        if (current === voice) current = null;
        try {
            if (voice.socket && voice.socket.readyState <= WebSocket.OPEN) voice.socket.close(1000, "Voice stopped");
        } catch {
            // The socket is already gone.
        }
        release(voice);
        if (state) notify(voice, state, message, code);
    }

    function onVisibilityChange() {
        const voice = current;
        if (!voice || voice.stopped || document.visibilityState !== "hidden") return;
        voice.micStream?.getTracks().forEach((track) => { track.enabled = false; });
        voice.micContext?.suspend().catch(() => {});
        voice.playContext?.suspend().catch(() => {});
        notify(voice, "paused");
    }

    document.addEventListener("visibilitychange", onVisibilityChange);
    window.addEventListener("pagehide", () => current && end(current, null));

    window.conciergeVoice = {
        /** Opens the relay for a verified chat. Must run from a guest click so audio may start. */
        async start(credential, voiceSessionId, dotNet) {
            if (current) end(current, null);
            const voice = { dotNet, state: null, stopped: false };
            current = voice;
            notify(voice, "connecting");
            try {
                await createPlayback(voice);
            } catch {
                end(voice, "disconnected", "Audio playback couldn't start in this browser.", 0);
                return;
            }
            if (voice.stopped) return;

            const scheme = location.protocol === "https:" ? "wss" : "ws";
            const query = voiceSessionId ? `?voiceSession=${encodeURIComponent(voiceSessionId)}` : "";
            const socket = new WebSocket(`${scheme}://${location.host}/api/concierge/voice${query}`, [protocol, credential]);
            socket.binaryType = "arraybuffer";
            voice.socket = socket;
            socket.onmessage = (event) => onSocketMessage(voice, event);
            socket.onclose = (event) => end(
                voice,
                "disconnected",
                closeMessages[event.code] ?? closeMessages[4000],
                event.code);
        },

        /** Resumes capture and playback after the page was hidden. */
        async resume() {
            const voice = current;
            if (!voice || voice.stopped) return;
            await voice.playContext?.resume();
            await voice.micContext?.resume();
            voice.micStream?.getTracks().forEach((track) => { track.enabled = true; });
            notify(voice, "listening");
        },

        stop() {
            if (current) end(current, null);
        },
    };
})();
