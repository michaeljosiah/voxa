import React from 'react';

/** VOXA IconButton — square hit target for icon-only actions. */
export function IconButton({ size = 'md', outline = false, label, children, disabled, onClick, style, className = '' }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      className={`vx-iconbtn ${size === 'sm' ? 'vx-iconbtn--sm' : ''} ${outline ? 'vx-iconbtn--outline' : ''} ${className}`}
      disabled={disabled}
      onClick={onClick}
      style={style}
    >
      {children}
    </button>
  );
}
