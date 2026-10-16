// Microphone capture: resamples the context rate (usually 48 kHz) to 16 kHz linear16 and posts
// 20 ms frames (320 samples), the input format the Voice Agent Settings declare.
class ConciergeMicProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.ratio = sampleRate / 16000;
        this.position = 0;
        this.previous = 0;
        this.frame = new Int16Array(320);
        this.length = 0;
    }

    process(inputs) {
        const channel = inputs[0] && inputs[0][0];
        if (!channel) return true;

        // Linear interpolation; index -1 is the last sample of the previous render quantum.
        while (this.position < channel.length - 1) {
            const index = Math.floor(this.position);
            const s0 = index < 0 ? this.previous : channel[index];
            const s1 = channel[index + 1];
            const value = Math.max(-1, Math.min(1, s0 + (s1 - s0) * (this.position - index)));
            this.frame[this.length++] = value < 0 ? value * 0x8000 : value * 0x7fff;
            if (this.length === this.frame.length) {
                this.port.postMessage(this.frame.buffer, [this.frame.buffer]);
                this.frame = new Int16Array(320);
                this.length = 0;
            }
            this.position += this.ratio;
        }

        this.position -= channel.length;
        this.previous = channel[channel.length - 1];
        return true;
    }
}

registerProcessor("concierge-mic", ConciergeMicProcessor);
