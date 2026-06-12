/**
 * VOXA Button.
 */
export interface ButtonProps {
  /** Visual emphasis. @default 'primary' */
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  /** Control height. @default 'md' */
  size?: 'sm' | 'md' | 'lg';
  /** Optional leading icon node (e.g. a Lucide <i data-lucide>). */
  icon?: React.ReactNode;
  children?: React.ReactNode;
  disabled?: boolean;
  onClick?: () => void;
  type?: 'button' | 'submit';
  style?: React.CSSProperties;
  className?: string;
}
export declare function Button(props: ButtonProps): JSX.Element;
