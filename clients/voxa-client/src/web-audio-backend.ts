import type { AudioBackend } from "./audio.js";

// The recorder worklet, inlined as a Blob so the package is a single import — no separate file for
// consumers to host. Ported verbatim from the proven reference implementation
// (samples/Voxa.Samples.MinimalServer/wwwroot/recorder-worklet.js).
const WORKLET_SOURCE = `
class VoxaRecorderProcessor extends AudioWorkletProcessor {
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
        this.port.postMessage(int16.buffer, [int16.buffer]);
        this.bufferIndex = 0;
      }
    }
    return true;
  }
}
registerProcessor("voxa-recorder", VoxaRecorderProcessor);
`;

/**
 * Default browser backend: AudioWorklet mic capture at the announced input rate, gap-free
 * playback scheduling at the announced output rate, and a flush() that stops audio already
 * handed to the Web Audio graph — the client half of barge-in the server cannot reach.
 */
export class WebAudioBackend implements AudioBackend {
  private readonly micConstraints: MediaTrackConstraints;
  private playCtx: AudioContext | null = null;
  private micCtx: AudioContext | null = null;
  private micStream: MediaStream | null = null;
  private worklet: AudioWorkletNode | null = null;
  private nextPlayTime = 0;
  private readonly scheduled = new Set<AudioBufferSourceNode>();

  constructor(micConstraints?: MediaTrackConstraints) {
    // Reference-page defaults: browser EC/NS/AGC on, mono.
    this.micConstraints = micConstraints ?? {
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    };
  }

  async startCapture(inputRate: number, onFrame: (pcm: Int16Array) => void): Promise<void> {
    this.micStream = await navigator.mediaDevices.getUserMedia({ audio: this.micConstraints });
    this.micCtx = new AudioContext({ sampleRate: inputRate });
    const workletUrl = URL.createObjectURL(new Blob([WORKLET_SOURCE], { type: "application/javascript" }));
    try {
      await this.micCtx.audioWorklet.addModule(workletUrl);
    } finally {
      URL.revokeObjectURL(workletUrl);
    }

    const source = this.micCtx.createMediaStreamSource(this.micStream);
    this.worklet = new AudioWorkletNode(this.micCtx, "voxa-recorder", {
      processorOptions: { chunkSamples: Math.floor(inputRate / 50) }, // 20 ms frames
    });
    this.worklet.port.onmessage = (e: MessageEvent<ArrayBuffer>) => onFrame(new Int16Array(e.data));
    source.connect(this.worklet);
  }

  play(pcm: Int16Array, outputRate: number): void {
    if (pcm.length === 0) return;
    this.playCtx ??= new AudioContext();
    const ctx = this.playCtx;
    const f32 = new Float32Array(pcm.length);
    for (let i = 0; i < pcm.length; i++) f32[i] = pcm[i] / 32768;
    const buf = ctx.createBuffer(1, f32.length, outputRate); // the context resamples to its own rate
    buf.copyToChannel(f32, 0);
    const src = ctx.createBufferSource();
    src.buffer = buf;
    src.connect(ctx.destination);
    const startAt = Math.max(ctx.currentTime, this.nextPlayTime);
    src.start(startAt);
    this.nextPlayTime = startAt + buf.duration;
    this.scheduled.add(src);
    src.onended = () => this.scheduled.delete(src);
  }

  flush(): void {
    for (const s of this.scheduled) {
      try { s.stop(); } catch { /* already stopped */ }
    }
    this.scheduled.clear();
    if (this.playCtx) this.nextPlayTime = this.playCtx.currentTime;
  }

  async stop(): Promise<void> {
    this.flush();
    try { this.worklet?.disconnect(); } catch { /* already disconnected */ }
    this.micStream?.getTracks().forEach((t) => t.stop());
    if (this.micCtx) { try { await this.micCtx.close(); } catch { /* already closed */ } }
    if (this.playCtx) { try { await this.playCtx.close(); } catch { /* already closed */ } }
    this.worklet = null;
    this.micStream = null;
    this.micCtx = null;
    this.playCtx = null;
    this.nextPlayTime = 0;
  }
}
