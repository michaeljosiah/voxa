import React from 'react';

/** VOXA Badge — uppercase mono pill for statuses and taxonomy. */
export function Badge({ tone = 'neutral', children, style, className = '' }) {
  return (
    <span className={`vx-badge vx-badge--${tone} ${className}`} style={style}>
      {children}
    </span>
  );
}
