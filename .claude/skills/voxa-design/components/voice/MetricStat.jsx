import React from 'react';

/** VOXA MetricStat — big mono number with label and delta. */
export function MetricStat({ label, value, unit, delta, deltaTone = 'flat', style, className = '' }) {
  return (
    <div className={`vx-metric ${className}`} style={style}>
      <span className="vx-label">{label}</span>
      <span className="vx-metric__value">{value}{unit && <small>{unit}</small>}</span>
      {delta && <span className={`vx-metric__delta vx-metric__delta--${deltaTone}`}>{delta}</span>}
    </div>
  );
}
