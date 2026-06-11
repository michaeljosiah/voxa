// Captures mic audio at the AudioContext's sample rate (the page sets it from the session
// envelope's inputSampleRate), buffers Float32 samples into fixed-size chunks, converts to
// 16-bit PCM, and posts each chunk to the main thread as a transferable ArrayBuffer.
class RecorderProcessor extends AudioWorkletProcessor {
  constructor(opts) {
    super();
    this.chunkSamples = opts?.processorOptions?.chunkSamples ?? 320;
    this.buffer = new Float32Array(this.chunkSamples);
    this.bufferIndex = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const channel = input[0];
    if (!channel) return true;

    for (let i = 0; i < channel.length; i++) {
      this.buffer[this.bufferIndex++] = channel[i];
      if (this.bufferIndex >= this.chunkSamples) {
        const int16 = new Int16Array(this.chunkSamples);
        for (let j = 0; j < this.chunkSamples; j++) {
          const s = Math.max(-1, Math.min(1, this.buffer[j]));
          int16[j] = s < 0 ? s * 0x8000 : s * 0x7fff;
        }
        // Transfer the underlying buffer for zero-copy.
        this.port.postMessage(int16.buffer, [int16.buffer]);
        this.bufferIndex = 0;
      }
    }
    return true;
  }
}

registerProcessor('recorder', RecorderProcessor);
