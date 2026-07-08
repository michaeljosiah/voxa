import type { AudioBackend } from "./audio.js";
import { Emitter } from "./events.js";
import { WebAudioBackend } from "./web-audio-backend.js";
import {
  VOXA_WIRE_VERSION,
  type ServerMessage,
  type SessionMessage,
  type TranscriptionMessage,
  type TextMessage,
  type ToolCallMessage,
  type SpeakingMessage,
  type StatusMessage,
  type ErrorMessage,
} from "./protocol.js";

/** The subset of the WebSocket surface the client uses — injectable for headless tests. */
export interface WebSocketLike {
  binaryType: string;
  readyState: number;
  send(data: string | ArrayBufferLike): void;
  close(code?: number, reason?: string): void;
  onopen: (() => void) | null;
  onmessage: ((event: { data: string | ArrayBuffer }) => void) | null;
  onclose: ((event: { code: number }) => void) | null;
  onerror: (() => void) | null;
}

export interface VoxaClientOptions {
  /** ws(s)://host/voice */
  url: string;
  /** Sent FIRST, before any audio — only when the server opted into UseWebSocketHello<T>. */
  hello?: Record<string, unknown>;
  /** What to do when session.v differs from VOXA_WIRE_VERSION. Default "warn". */
  onVersionMismatch?: "warn" | "throw" | "ignore";
  /** Audio engine. Defaults to WebAudioBackend; inject a fake for tests. */
  audio?: AudioBackend;
  /** Mic constraints for the default backend (browser EC/NS/AGC on, mono, by default). */
  micConstraints?: MediaTrackConstraints;
  /** WebSocket factory — injectable for tests; defaults to the platform WebSocket. */
  socketFactory?: (url: string) => WebSocketLike;
}

export type VoxaEvents = {
  session: (s: { v: number; inputSampleRate: number; outputSampleRate: number }) => void;
  transcription: (t: { text: string; isFinal: boolean; language?: string | null; speakerId?: string | null }) => void;
  /** Streamed assistant tokens. */
  text: (t: { text: string }) => void;
  speaking: (s: { who: "bot" | "user"; started: boolean }) => void;
  toolCall: (c: { callId: string; name: string; argumentsJson: string }) => void;
  /** Barge-in: local playback was flushed (interruption envelope or user started speaking). */
  interruption: () => void;
  status: (m: { message: string }) => void;
  error: (e: { message: string }) => void;
  /** Mic level 0..1 per capture frame — convenience for meters. */
  micLevel: (rms: number) => void;
  end: () => void;
};

const OPEN = 1; // WebSocket.OPEN, without requiring the DOM global in tests

/**
 * The client half of the Voxa voice loop: connect → session (rates + version) → mic capture →
 * typed events → gap-free playback → flush-on-barge-in → frontend-tool round-trips.
 * A faithful peer of the server's wire protocol; adds no envelope types.
 */
export class VoxaClient extends Emitter<VoxaEvents> {
  private readonly opts: VoxaClientOptions;
  private readonly audio: AudioBackend;
  private socket: WebSocketLike | null = null;
  private session: SessionMessage | null = null;
  private ended = false;

  constructor(opts: VoxaClientOptions) {
    super();
    this.opts = opts;
    this.audio = opts.audio ?? new WebAudioBackend(opts.micConstraints);
  }

  /** Opens the socket; resolves once the session envelope arrived and the mic is live. */
  connect(): Promise<void> {
    if (this.socket) throw new Error("VoxaClient is already connected.");
    return new Promise<void>((resolve, reject) => {
      let settled = false;
      const fail = (err: Error) => {
        if (settled) return;
        settled = true;
        reject(err);
      };

      const socket = this.opts.socketFactory
        ? this.opts.socketFactory(this.opts.url)
        : (new WebSocket(this.opts.url) as unknown as WebSocketLike);
      this.socket = socket;
      socket.binaryType = "arraybuffer";

      socket.onopen = () => {
        // Hello ordering: the one out-of-band message a configured server reads, sent before
        // any audio. Servers that didn't opt in drop unknown text envelopes, but we still only
        // send it when the host asked to.
        if (this.opts.hello) socket.send(JSON.stringify({ type: "hello", ...this.opts.hello }));
      };

      socket.onmessage = (event) => {
        if (typeof event.data === "string") {
          this.handleEnvelope(event.data, { resolveConnect: () => { if (!settled) { settled = true; resolve(); } }, fail });
        } else if (this.session && event.data.byteLength % 2 === 0) {
          this.audio.play(new Int16Array(event.data), this.session.outputSampleRate);
        }
      };

      socket.onclose = () => {
        this.emitEndOnce();
        fail(new Error("WebSocket closed before the session envelope arrived."));
      };
      socket.onerror = () => {
        this.emit("error", { message: "WebSocket error." });
        fail(new Error("WebSocket error before the session envelope arrived."));
      };
    });
  }

  /** Optional text-only turn (no mic needed). */
  sendText(text: string): void {
    this.send(JSON.stringify({ type: "text", text }));
  }

  /** Round-trip a frontend tool result back to the agent loop. */
  sendToolResult(callId: string, resultJson: string, isError?: boolean): void {
    this.send(JSON.stringify(
      isError === undefined
        ? { type: "toolResult", callId, resultJson }
        : { type: "toolResult", callId, resultJson, isError }));
  }

  /** Sends {type:"end"} — asks the server to finish the session. */
  end(): void {
    this.send(JSON.stringify({ type: "end" }));
  }

  /** Close the socket and tear down audio. */
  async disconnect(): Promise<void> {
    try { this.socket?.close(1000); } catch { /* already closed */ }
    this.socket = null;
    this.session = null;
    await this.audio.stop();
  }

  private send(json: string): void {
    if (this.socket?.readyState === OPEN) this.socket.send(json);
  }

  private handleEnvelope(
    json: string,
    connect: { resolveConnect: () => void; fail: (err: Error) => void },
  ): void {
    let msg: ServerMessage;
    try {
      msg = JSON.parse(json) as ServerMessage;
    } catch {
      return; // drop malformed, mirroring the server's dropped-unknown discipline
    }

    switch (msg.type) {
      case "session":
        this.handleSession(msg, connect);
        break;
      case "transcription": {
        const t = msg as TranscriptionMessage;
        this.emit("transcription", { text: t.text, isFinal: t.isFinal, language: t.language, speakerId: t.speakerId });
        break;
      }
      case "text":
        this.emit("text", { text: (msg as TextMessage).text });
        break;
      case "toolCall": {
        const c = msg as ToolCallMessage;
        this.emit("toolCall", { callId: c.callId, name: c.name, argumentsJson: c.argumentsJson });
        break;
      }
      case "speaking": {
        const s = msg as SpeakingMessage;
        // Barge-in trigger #1: the user started talking — stop local playback before the
        // interruption envelope even arrives.
        if (s.who === "user" && s.started) {
          this.audio.flush();
          this.emit("interruption");
        }
        this.emit("speaking", { who: s.who, started: s.started });
        break;
      }
      case "interruption":
        // Barge-in trigger #2: the server's explicit purge marker.
        this.audio.flush();
        this.emit("interruption");
        break;
      case "status":
        this.emit("status", { message: (msg as StatusMessage).message });
        break;
      case "error":
        this.emit("error", { message: (msg as ErrorMessage).message });
        break;
      case "end":
        this.emitEndOnce();
        break;
      default:
        break; // unknown envelope from a newer server — ignore, per the version policy
    }
  }

  private handleSession(
    msg: SessionMessage,
    connect: { resolveConnect: () => void; fail: (err: Error) => void },
  ): void {
    const policy = this.opts.onVersionMismatch ?? "warn";
    if (msg.v !== VOXA_WIRE_VERSION && policy !== "ignore") {
      const detail = `Voxa wire version mismatch: server v${msg.v}, client v${VOXA_WIRE_VERSION}.`;
      if (policy === "throw") {
        connect.fail(new Error(detail));
        this.socket?.close(1002);
        return;
      }
      console.warn(`${detail} Unknown envelopes will be ignored.`);
    }

    this.session = msg;
    this.emit("session", { v: msg.v, inputSampleRate: msg.inputSampleRate, outputSampleRate: msg.outputSampleRate });

    this.audio
      .startCapture(msg.inputSampleRate, (pcm) => {
        this.emit("micLevel", rms(pcm));
        if (this.socket?.readyState === OPEN) this.socket.send(pcm.buffer);
      })
      .then(connect.resolveConnect)
      .catch((err: unknown) => {
        this.emit("error", { message: `Mic capture failed: ${err instanceof Error ? err.message : String(err)}` });
        connect.fail(err instanceof Error ? err : new Error(String(err)));
      });
  }

  private emitEndOnce(): void {
    if (this.ended) return;
    this.ended = true;
    this.emit("end");
  }
}

function rms(pcm: Int16Array): number {
  if (pcm.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < pcm.length; i++) {
    const s = pcm[i] / 32768;
    sum += s * s;
  }
  return Math.sqrt(sum / pcm.length);
}
