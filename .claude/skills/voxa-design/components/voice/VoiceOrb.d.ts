/**
 * VOXA VoiceOrb — agent presence orb.
 */
export interface VoiceOrbProps {
  /** Diameter in px. @default 96 */
  size?: number;
  /** Emit ripple rings while the agent is speaking/listening. @default false */
  live?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function VoiceOrb(props: VoiceOrbProps): JSX.Element;
