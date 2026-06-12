/**
 * VOXA Waveform — voice activity bars.
 */
export interface WaveformProps {
  /** Number of bars. @default 12 */
  bars?: number;
  /** Total height in px. @default 28 */
  height?: number;
  /** Animate bars bouncing. @default false */
  live?: boolean;
  /** Deprecated — the brand has no gradient; renders solid cyan. @default false */
  gradient?: boolean;
  /** Grey bars for inactive/muted streams. @default false */
  muted?: boolean;
  /** Custom 0–1 amplitude per bar (cycles if shorter than bars). */
  levels?: number[];
  style?: React.CSSProperties;
  className?: string;
}
export declare function Waveform(props: WaveformProps): JSX.Element;
