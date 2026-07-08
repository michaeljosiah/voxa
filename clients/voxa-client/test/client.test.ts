import { test } from "node:test";
import assert from "node:assert/strict";
import { VoxaClient } from "../src/client.js";
import type { WebSocketLike } from "../src/client.js";
import type { AudioBackend } from "../src/audio.js";

// ── fakes ────────────────────────────────────────────────────────────────────────────────────

class FakeSocket implements WebSocketLike {
  binaryType = "blob";
  readyState = 0;
  sent: (string | ArrayBufferLike)[] = [];
  closed: number | undefined;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string | ArrayBuffer }) => void) | null = null;
  onclose: ((event: { code: number }) => void) | null = null;
  onerror: (() => void) | null = null;

  send(data: string | ArrayBufferLike): void { this.sent.push(data); }
  close(code?: number): void { this.closed = code ?? 1000; }

  open(): void { this.readyState = 1; this.onopen?.(); }
  message(data: string | ArrayBuffer): void { this.onmessage?.({ data }); }
  sentJson(): unknown[] { return this.sent.filter((d): d is string => typeof d === "string").map((d) => JSON.parse(d)); }
}

class FakeAudio implements AudioBackend {
  captureRate: number | undefined;
  onFrame: ((pcm: Int16Array) => void) | undefined;
  played: { pcm: Int16Array; rate: number }[] = [];
  flushes = 0;
  stopped = false;
  failCapture = false;

  async startCapture(inputRate: number, onFrame: (pcm: Int16Array) => void): Promise<void> {
    if (this.failCapture) throw new Error("no mic");
    this.captureRate = inputRate;
    this.onFrame = onFrame;
  }
  play(pcm: Int16Array, outputRate: number): void { this.played.push({ pcm, rate: outputRate }); }
  flush(): void { this.flushes++; }
  async stop(): Promise<void> { this.stopped = true; }
}

const SESSION = JSON.stringify({ type: "session", v: 1, inputSampleRate: 16000, outputSampleRate: 24000 });

function harness(opts?: { hello?: Record<string, unknown>; onVersionMismatch?: "warn" | "throw" | "ignore" }) {
  const socket = new FakeSocket();
  const audio = new FakeAudio();
  const client = new VoxaClient({
    url: "ws://test/voice",
    audio,
    socketFactory: () => socket,
    ...opts,
  });
  return { socket, audio, client };
}

async function connected(opts?: Parameters<typeof harness>[0]) {
  const h = harness(opts);
  const connecting = h.client.connect();
  h.socket.open();
  h.socket.message(SESSION);
  await connecting;
  return h;
}

// ── session plumbing ─────────────────────────────────────────────────────────────────────────

test("connect resolves after session; both rates come from the envelope, nothing hardcoded", async () => {
  const { socket, audio, client } = harness();
  const sessions: unknown[] = [];
  client.on("session", (s) => sessions.push(s));

  const connecting = client.connect();
  socket.open();
  socket.message(SESSION);
  await connecting;

  assert.equal(audio.captureRate, 16000);
  assert.deepEqual(sessions, [{ v: 1, inputSampleRate: 16000, outputSampleRate: 24000 }]);
  assert.equal(socket.binaryType, "arraybuffer");

  const pcm = new Int16Array([1, 2, 3]);
  socket.message(pcm.buffer as ArrayBuffer);
  assert.equal(audio.played.length, 1);
  assert.equal(audio.played[0].rate, 24000);
});

test("binary audio before the session envelope is ignored, not misplayed", async () => {
  const { socket, audio, client } = harness();
  const connecting = client.connect();
  socket.open();
  socket.message(new Int16Array([9, 9]).buffer as ArrayBuffer);
  assert.equal(audio.played.length, 0);
  socket.message(SESSION);
  await connecting;
});

test("hello is sent first, before any audio, only when provided", async () => {
  const withHello = await connected({ hello: { user: "ada" } });
  assert.deepEqual(withHello.socket.sentJson()[0], { type: "hello", user: "ada" });

  const without = await connected();
  assert.equal(without.socket.sentJson().length, 0);
});

test("mic frames are forwarded as binary and micLevel is emitted", async () => {
  const { socket, audio, client } = await connected();
  const levels: number[] = [];
  client.on("micLevel", (rms) => levels.push(rms));

  audio.onFrame!(new Int16Array([16384, -16384])); // ±0.5 → rms 0.5
  const binary = socket.sent.filter((d) => typeof d !== "string");
  assert.equal(binary.length, 1);
  assert.equal(levels.length, 1);
  assert.ok(Math.abs(levels[0] - 0.5) < 0.001);
});

test("mic failure rejects connect and surfaces an error event", async () => {
  const { socket, audio, client } = harness();
  audio.failCapture = true;
  const errors: { message: string }[] = [];
  client.on("error", (e) => errors.push(e));

  const connecting = client.connect();
  socket.open();
  socket.message(SESSION);

  await assert.rejects(connecting, /no mic/);
  assert.equal(errors.length, 1);
});

// ── typed events ─────────────────────────────────────────────────────────────────────────────

test("transcription, text, status, error and end surface as typed events", async () => {
  const { socket, client } = await connected();
  const seen: Record<string, unknown[]> = { transcription: [], text: [], status: [], error: [], end: [] };
  client.on("transcription", (t) => seen.transcription.push(t));
  client.on("text", (t) => seen.text.push(t));
  client.on("status", (m) => seen.status.push(m));
  client.on("error", (e) => seen.error.push(e));
  client.on("end", () => seen.end.push(true));

  socket.message(JSON.stringify({ type: "transcription", text: "hi", isFinal: true, language: "en" }));
  socket.message(JSON.stringify({ type: "text", text: "Hello" }));
  socket.message(JSON.stringify({ type: "status", message: "warm" }));
  socket.message(JSON.stringify({ type: "error", message: "boom" }));
  socket.message(JSON.stringify({ type: "end" }));
  socket.message(JSON.stringify({ type: "end" })); // end is emitted once

  assert.deepEqual(seen.transcription, [{ text: "hi", isFinal: true, language: "en", speakerId: undefined }]);
  assert.deepEqual(seen.text, [{ text: "Hello" }]);
  assert.deepEqual(seen.status, [{ message: "warm" }]);
  assert.deepEqual(seen.error, [{ message: "boom" }]);
  assert.equal(seen.end.length, 1);
});

test("unknown envelopes and malformed JSON are dropped without faulting", async () => {
  const { socket } = await connected();
  socket.message(JSON.stringify({ type: "somethingNew", x: 1 }));
  socket.message("not json {");
});

// ── barge-in: the load-bearing behaviour ─────────────────────────────────────────────────────

test("interruption envelope flushes scheduled playback", async () => {
  const { socket, audio, client } = await connected();
  const interruptions: number[] = [];
  client.on("interruption", () => interruptions.push(1));

  socket.message(new Int16Array([1, 2]).buffer as ArrayBuffer);
  socket.message(JSON.stringify({ type: "interruption" }));

  assert.equal(audio.flushes, 1);
  assert.equal(interruptions.length, 1);
});

test("speaking{user,started} flushes playback before the interruption envelope arrives", async () => {
  const { socket, audio, client } = await connected();
  const speaking: unknown[] = [];
  client.on("speaking", (s) => speaking.push(s));

  socket.message(JSON.stringify({ type: "speaking", who: "user", started: true }));
  assert.equal(audio.flushes, 1);

  // The other three speaking transitions must NOT flush.
  socket.message(JSON.stringify({ type: "speaking", who: "user", started: false }));
  socket.message(JSON.stringify({ type: "speaking", who: "bot", started: true }));
  socket.message(JSON.stringify({ type: "speaking", who: "bot", started: false }));
  assert.equal(audio.flushes, 1);
  assert.equal(speaking.length, 4);
});

// ── frontend tools ───────────────────────────────────────────────────────────────────────────

test("toolCall surfaces and sendToolResult round-trips the exact envelope", async () => {
  const { socket, client } = await connected();
  const calls: unknown[] = [];
  client.on("toolCall", (c) => calls.push(c));

  socket.message(JSON.stringify({ type: "toolCall", callId: "c1", name: "lookup", argumentsJson: "{\"q\":1}" }));
  assert.deepEqual(calls, [{ callId: "c1", name: "lookup", argumentsJson: "{\"q\":1}" }]);

  client.sendToolResult("c1", "{\"ok\":true}");
  client.sendToolResult("c2", "{}", true);
  assert.deepEqual(socket.sentJson(), [
    { type: "toolResult", callId: "c1", resultJson: "{\"ok\":true}" },
    { type: "toolResult", callId: "c2", resultJson: "{}", isError: true },
  ]);
});

test("sendText and end serialize the client envelopes", async () => {
  const { socket, client } = await connected();
  client.sendText("type this");
  client.end();
  assert.deepEqual(socket.sentJson(), [
    { type: "text", text: "type this" },
    { type: "end" },
  ]);
});

// ── version policy ───────────────────────────────────────────────────────────────────────────

test("version mismatch: default warns and proceeds", async () => {
  const { socket, audio, client } = harness();
  const warnings: unknown[] = [];
  const original = console.warn;
  console.warn = (...args: unknown[]) => warnings.push(args);
  try {
    const connecting = client.connect();
    socket.open();
    socket.message(JSON.stringify({ type: "session", v: 2, inputSampleRate: 16000, outputSampleRate: 24000 }));
    await connecting;
  } finally {
    console.warn = original;
  }
  assert.equal(warnings.length, 1);
  assert.equal(audio.captureRate, 16000); // proceeded
});

test("version mismatch: throw policy rejects connect and closes the socket", async () => {
  const { socket, client } = harness({ onVersionMismatch: "throw" });
  const connecting = client.connect();
  socket.open();
  socket.message(JSON.stringify({ type: "session", v: 2, inputSampleRate: 16000, outputSampleRate: 24000 }));
  await assert.rejects(connecting, /wire version mismatch/);
  assert.equal(socket.closed, 1002);
});

test("version mismatch: ignore policy is silent", async () => {
  const { socket, audio, client } = harness({ onVersionMismatch: "ignore" });
  const warnings: unknown[] = [];
  const original = console.warn;
  console.warn = (...args: unknown[]) => warnings.push(args);
  try {
    const connecting = client.connect();
    socket.open();
    socket.message(JSON.stringify({ type: "session", v: 99, inputSampleRate: 16000, outputSampleRate: 24000 }));
    await connecting;
  } finally {
    console.warn = original;
  }
  assert.equal(warnings.length, 0);
  assert.equal(audio.captureRate, 16000);
});

// ── lifecycle ────────────────────────────────────────────────────────────────────────────────

test("disconnect closes the socket and tears down audio", async () => {
  const { socket, audio, client } = await connected();
  await client.disconnect();
  assert.equal(socket.closed, 1000);
  assert.equal(audio.stopped, true);
});

test("socket close emits end (once) and unsubscribe works", async () => {
  const { socket, client } = await connected();
  let ends = 0;
  const off = client.on("end", () => ends++);
  socket.onclose?.({ code: 1000 });
  assert.equal(ends, 1);
  off();
  socket.onclose?.({ code: 1000 });
  assert.equal(ends, 1);
});
