/** VOXA Badge — uppercase mono pill. */
export interface BadgeProps {
  /** @default 'neutral' */
  tone?: 'neutral' | 'pulse' | 'halo' | 'ok' | 'warn' | 'danger' | 'info';
  children: React.ReactNode;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Badge(props: BadgeProps): JSX.Element;
