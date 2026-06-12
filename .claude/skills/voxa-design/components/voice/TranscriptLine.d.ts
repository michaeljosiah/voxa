/** VOXA TranscriptLine — one utterance in a session transcript. */
export interface TranscriptLineProps {
  /** Speaker — colors the role gutter: user blue, agent cyan, system grey mono. @default 'user' */
  role?: 'user' | 'agent' | 'system';
  text: string;
  /** Mono timestamp, e.g. "00:42.118". */
  time?: string;
  /** In-progress utterance — dimmed with a blinking caret. @default false */
  partial?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function TranscriptLine(props: TranscriptLineProps): JSX.Element;
