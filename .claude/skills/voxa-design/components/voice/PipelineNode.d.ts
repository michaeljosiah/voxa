/** VOXA PipelineNode — one processor in a frame pipeline. */
export interface PipelineNodeProps {
  /** Stage semantic → fixed stage color: vad grey, stt cyan, agent violet, tts amber, out green. @default 'stt' */
  stage?: 'vad' | 'stt' | 'agent' | 'tts' | 'out' | 'audio' | 'llm';
  /** Uppercase kind label; defaults to the stage name. */
  kind?: string;
  /** Processor display name, e.g. "AzureSpeechStt". */
  name: string;
  /** Mono metadata line, e.g. "p50 118 ms". */
  meta?: string;
  /** Highlight as currently processing. @default false */
  active?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function PipelineNode(props: PipelineNodeProps): JSX.Element;
