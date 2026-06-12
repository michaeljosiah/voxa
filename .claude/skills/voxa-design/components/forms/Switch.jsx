import React, { useState } from 'react';

/** VOXA Switch — toggle with optional inline label. */
export function Switch({ checked, defaultChecked = false, onChange, label, disabled, style, className = '' }) {
  const [internal, setInternal] = useState(defaultChecked);
  const isOn = checked ?? internal;
  const toggle = () => { setInternal(!isOn); onChange && onChange(!isOn); };
  const btn = (
    <button
      type="button"
      role="switch"
      aria-checked={isOn}
      className={`vx-switch ${className}`}
      onClick={toggle}
      disabled={disabled}
      style={label ? undefined : style}
    />
  );
  if (!label) return btn;
  return (
    <span className="vx-switch-row" style={style}>
      {btn}
      <span onClick={disabled ? undefined : toggle} style={{ cursor: disabled ? 'default' : 'pointer' }}>{label}</span>
    </span>
  );
}
