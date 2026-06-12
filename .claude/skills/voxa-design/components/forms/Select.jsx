import React from 'react';

/** VOXA Select — labeled native select, styled. */
export function Select({ label, hint, options, value, defaultValue, onChange, disabled, style, className = '' }) {
  return (
    <div className={`vx-field ${className}`} style={style}>
      {label && <label>{label}</label>}
      <select
        className="vx-select"
        value={value}
        defaultValue={defaultValue}
        onChange={onChange ? (e) => onChange(e.target.value) : undefined}
        disabled={disabled}
      >
        {options.map((o) => {
          const opt = typeof o === 'string' ? { value: o, label: o } : o;
          return <option key={opt.value} value={opt.value}>{opt.label}</option>;
        })}
      </select>
      {hint && <span className="vx-field__hint">{hint}</span>}
    </div>
  );
}
