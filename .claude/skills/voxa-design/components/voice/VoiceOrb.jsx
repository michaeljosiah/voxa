import React from 'react';

/** VOXA VoiceOrb — the agent presence orb. */
export function VoiceOrb({ size = 96, live = false, style, className = '' }) {
  return (
    <span
      className={`vx-orb ${live ? 'vx-orb--live' : ''} ${className}`}
      style={{ display: 'inline-block', width: size, height: size, ...style }}
      aria-hidden="true"
    />
  );
}
