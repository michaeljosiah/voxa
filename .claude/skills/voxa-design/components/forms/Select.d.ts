/** VOXA Select — labeled native select. */
export interface SelectProps {
  label?: string;
  hint?: string;
  /** Strings, or {value,label} pairs. */
  options: Array<string | { value: string; label: string }>;
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Select(props: SelectProps): JSX.Element;
