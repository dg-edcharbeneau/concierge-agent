// Agent playback: a bounded 24 kHz ring buffer. The server paces audio against the cumulative
// "played" sample count acknowledged from here, so a flush must report the samples it retired.
class ConciergePlayProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.size = 24000 * 180;
        this.buffer = new Float32Array(this.size);
        this.read = 0;
        this.written = 0;
        this.lastAck = 0;
        this.playing = false;
        this.flushed = false;
        this.pendingSilenceAcks = [];
        this.lastBlockHadAudio = false;
        this.port.onmessage = (event) => {
            const message = event.data;
            if (message.type === "audio") {
                const samples = message.samples;
                if (this.written - this.read + samples.length > this.size) {
                    this.port.postMessage({ type: "overflow" });
                    return;
                }
                for (let i = 0; i < samples.length; i++) {
                    this.buffer[(this.written + i) % this.size] = samples[i];
                }
                this.written += samples.length;
            } else if (message.type === "flush") {
                // Queued/server-paced samples are not proof that the browser rendered speech.
                // Capture the last render block before discarding, once per interruption.
                const bargeIn = this.lastBlockHadAudio;
                this.lastBlockHadAudio = false;
                this.read = this.written;
                this.flushed = true;
                if (Number.isSafeInteger(message.interruptionNumber) && message.interruptionNumber > 0) {
                    this.pendingSilenceAcks.push({ interruptionNumber: message.interruptionNumber, bargeIn });
                }
            }
        };
    }

    process(_inputs, outputs) {
        const output = outputs[0][0];
        const interruptionSilence = this.pendingSilenceAcks.length > 0;
        const hadAudio = !interruptionSilence && this.read < this.written;
        let renderedAudio = false;
        // A fresh response can be queued before this render quantum; acknowledge only after an all-zero block.
        for (let i = 0; i < output.length; i++) {
            output[i] = !interruptionSilence && this.read < this.written
                ? this.buffer[this.read++ % this.size]
                : 0;
            renderedAudio ||= output[i] !== 0;
        }
        this.lastBlockHadAudio = renderedAudio;

        for (const acknowledgment of this.pendingSilenceAcks) {
            this.port.postMessage({ type: "silenced", ...acknowledgment });
        }
        this.pendingSilenceAcks.length = 0;

        if (this.flushed || this.read - this.lastAck >= 480) {
            this.flushed = false;
            this.lastAck = this.read;
            this.port.postMessage({ type: "played", samples: this.read });
        }

        if (hadAudio && !this.playing) {
            this.playing = true;
            this.port.postMessage({ type: "speaking" });
        } else if (!hadAudio && this.playing) {
            this.playing = false;
            this.port.postMessage({ type: "drained" });
        }
        return true;
    }
}

registerProcessor("concierge-play", ConciergePlayProcessor);
