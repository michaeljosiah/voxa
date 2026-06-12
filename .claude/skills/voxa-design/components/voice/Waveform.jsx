import React from 'react';

const STATIC_LEVELS = [0.5, 0.85, 0.4, 1, 0.65, 0.9, 0.35, 0.7, 1, 0.55, 0.8, 0.45];

/** VOXA Waveform — voice activity bars (the brand's core motif). */
export function Waveform({ bars = 12, height = 28, live = false, gradient = false, muted = false, levels, style, className = '' }) {
  const lv = levels && levels.length ? levels : STATIC_LEVELS;
  return (
    <span
      className={`vx-wave ${live ? 'vx-wave--live' : ''} ${gradient ? 'vx-wave--grad' : ''} ${muted ? 'vx-wave--muted' : ''} ${className}`}
      style={{ height, ...style }}
      aria-hidden="true"
    >
      {Array.from({ length: bars }).map((_, i) => (
        <span
          key={i}
          className="vx-wave__bar"
          style={{
            height: `${Math.round(lv[i % lv.length] * 100)}%`,
            animationDelay: live ? `${(i * 87) % 900}ms` : undefined,
          }}
        />
      ))}
    </span>
  );
}
