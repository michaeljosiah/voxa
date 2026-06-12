/** VOXA Input — labeled text field. */
export interface InputProps {
  label?: string;
  /** Helper text under the field. */
  hint?: string;
  /** Error message — replaces hint and turns the border red. */
  error?: string;
  /** Mono font for keys, IDs, endpoints. @default false */
  mono?: boolean;
  type?: string;
  value?: string;
  defaultValue?: string;
  placeholder?: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Input(props: InputProps): JSX.Element;
