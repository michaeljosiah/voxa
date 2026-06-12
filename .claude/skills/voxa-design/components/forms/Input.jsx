import React from 'react';

/** VOXA Input — labeled text field. */
export function Input({ label, hint, error, mono = false, type = 'text', value, defaultValue, placeholder, onChange, disabled, style, className = '' }) {
  return (
    <div className={`vx-field ${error ? 'vx-field--error' : ''} ${className}`} style={style}>
      {label && <label>{label}</label>}
      <input
        className={`vx-input ${mono ? 'vx-input--mono' : ''}`}
        type={type}
        value={value}
        defaultValue={defaultValue}
        placeholder={placeholder}
        onChange={onChange ? (e) => onChange(e.target.value) : undefined}
        disabled={disabled}
      />
      {(error || hint) && <span className="vx-field__hint">{error || hint}</span>}
    </div>
  );
}
