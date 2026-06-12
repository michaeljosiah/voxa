import React from 'react';

const LABELS = { live: 'Live', connecting: 'Connecting', idle: 'Idle', ended: 'Ended', error: 'Error' };

/** VOXA StatusPill — session/pipeline state with signal dot. */
export function StatusPill({ status = 'idle', label, style, className = '' }) {
  return (
    <span className={`vx-status vx-status--${status} ${className}`} style={style}>
      <span className="vx-status__dot" />
      {label ?? LABELS[status] ?? status}
    </span>
  );
}
