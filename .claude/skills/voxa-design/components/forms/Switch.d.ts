/** VOXA Switch — boolean toggle. */
export interface SwitchProps {
  /** Controlled state. */
  checked?: boolean;
  /** Uncontrolled initial state. @default false */
  defaultChecked?: boolean;
  onChange?: (checked: boolean) => void;
  /** Inline label rendered to the right. */
  label?: string;
  disabled?: boolean;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Switch(props: SwitchProps): JSX.Element;
