import type { PipelineNodeProps } from './PipelineNode';

/**
 * VOXA PipelineFlow — a chain of pipeline nodes joined by dashed frame-travel links.
 */
export interface PipelineFlowProps {
  /** Node definitions, in order. */
  nodes: PipelineNodeProps[];
  /** Animate frames traveling along the links. @default false */
  running?: boolean;
  /** Index of the node to highlight as active. @default -1 */
  activeIndex?: number;
  style?: React.CSSProperties;
  className?: string;
}
export declare function PipelineFlow(props: PipelineFlowProps): JSX.Element;
