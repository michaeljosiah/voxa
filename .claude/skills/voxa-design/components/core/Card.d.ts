/**
 * VOXA Card — raised dark surface.
 */
export interface CardProps {
  /** 20px inner padding. @default true */
  padded?: boolean;
  /** Hover lift + pointer cursor. @default false */
  interactive?: boolean;
  /** Cyan glow border — for the one highlighted item. @default false */
  glow?: boolean;
  children: React.ReactNode;
  onClick?: () => void;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Card(props: CardProps): JSX.Element;
