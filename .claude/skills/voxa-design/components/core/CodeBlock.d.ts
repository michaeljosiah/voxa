/** VOXA CodeBlock — dark code panel with filename bar. */
export interface CodeBlockProps {
  /** Source code (rendered with light C#-flavored highlighting). */
  code: string;
  /** Filename shown in the header bar, e.g. "Program.cs". */
  file?: string;
  /** Small badge text in the header, e.g. "C#". */
  badge?: string;
  /** Optional header actions (e.g. a copy IconButton). */
  actions?: React.ReactNode;
  style?: React.CSSProperties;
  className?: string;
}
export declare function CodeBlock(props: CodeBlockProps): JSX.Element;
