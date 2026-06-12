import React from 'react';

/** VOXA Button — primary / secondary / ghost / danger. */
export function Button({ variant = 'primary', size = 'md', icon, children, disabled, onClick, type = 'button', style, className = '' }) {
  return (
    <button
      type={type}
      className={`vx-btn vx-btn--${variant} vx-btn--${size} ${className}`}
      disabled={disabled}
      onClick={onClick}
      style={style}
    >
      {icon}
      {children}
    </button>
  );
}
