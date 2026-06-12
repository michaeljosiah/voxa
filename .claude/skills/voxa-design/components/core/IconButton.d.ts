/** VOXA IconButton — icon-only square button. */
export interface IconButtonProps {
  /** @default 'md' (38px); 'sm' is 30px */
  size?: 'sm' | 'md';
  /** Adds border + surface fill. */
  outline?: boolean;
  /** Accessible label (also used as tooltip). Required. */
  label: string;
  /** The icon node. */
  children: React.ReactNode;
  disabled?: boolean;
  onClick?: () => void;
  style?: React.CSSProperties;
  className?: string;
}
export declare function IconButton(props: IconButtonProps): JSX.Element;
