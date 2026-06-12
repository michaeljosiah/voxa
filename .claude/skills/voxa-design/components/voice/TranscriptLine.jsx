import React from 'react';

/** VOXA TranscriptLine — one utterance in a session transcript. */
export function TranscriptLine({ role = 'user', text, time, partial = false, style, className = '' }) {
  return (
    <div className={`vx-transcript-line vx-transcript-line--${role} ${partial ? 'vx-transcript-line--partial' : ''} ${className}`} style={style}>
      <span className="vx-transcript-line__role">{role}</span>
      <span className="vx-transcript-line__text">{text}</span>
      {time && <span className="vx-transcript-line__time">{time}</span>}
    </div>
  );
}
