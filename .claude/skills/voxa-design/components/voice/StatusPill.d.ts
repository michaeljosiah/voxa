/** VOXA StatusPill — session/pipeline state. */
export interface StatusPillProps {
  /** @default 'idle' */
  status?: 'live' | 'connecting' | 'idle' | 'ended' | 'error';
  /** Override the default label text. */
  label?: string;
  style?: React.CSSProperties;
  className?: string;
}
export declare function StatusPill(props: StatusPillProps): JSX.Element;
