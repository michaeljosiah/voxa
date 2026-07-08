// All audio lives behind this seam so the client state machine is unit-testable headless —
// the same rule Voxa applies to its own engines and transports.
export interface AudioBackend {
  /** Start mic capture at the announced input rate; deliver ~20 ms Int16 frames to onFrame. */
  startCapture(inputRate: number, onFrame: (pcm: Int16Array) => void): Promise<void>;

  /** Schedule PCM gap-free at the announced output rate. */
  play(pcm: Int16Array, outputRate: number): void;

  /** Barge-in: stop everything already scheduled and reset the play cursor. */
  flush(): void;

  /** Tear down capture and playback. */
  stop(): Promise<void>;
}
