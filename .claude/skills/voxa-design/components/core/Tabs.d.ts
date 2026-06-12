/** VOXA Tabs — underline view switcher. */
export interface TabsProps {
  /** Tab labels (also the values passed to onChange). */
  tabs: string[];
  /** Controlled active tab. */
  active?: string;
  /** Uncontrolled initial tab. @default tabs[0] */
  defaultTab?: string;
  onChange?: (tab: string) => void;
  style?: React.CSSProperties;
  className?: string;
}
export declare function Tabs(props: TabsProps): JSX.Element;
