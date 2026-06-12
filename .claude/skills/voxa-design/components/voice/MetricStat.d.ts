/** VOXA MetricStat — labeled mono metric with optional delta. */
export interface MetricStatProps {
  /** Uppercase micro-label, e.g. "TTFB". */
  label: string;
  /** The number, pre-formatted, e.g. "412". */
  value: string;
  /** Unit suffix rendered small, e.g. "ms". */
  unit?: string;
  /** Delta line, e.g. "▼ 38 ms vs yesterday". */
  delta?: string;
  /** @default 'flat' */
  deltaTone?: 'good' | 'bad' | 'flat';
  style?: React.CSSProperties;
  className?: string;
}
export declare function MetricStat(props: MetricStatProps): JSX.Element;
