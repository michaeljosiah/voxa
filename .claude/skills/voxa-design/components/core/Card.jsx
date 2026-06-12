import React from 'react';

/** VOXA Card — raised dark surface. */
export function Card({ padded = true, interactive = false, glow = false, children, onClick, style, className = '' }) {
  return (
    <div
      className={`vx-card ${padded ? 'vx-card--pad' : ''} ${interactive ? 'vx-card--interactive' : ''} ${glow ? 'vx-card--glow' : ''} ${className}`}
      onClick={onClick}
      style={style}
    >
      {children}
    </div>
  );
}
