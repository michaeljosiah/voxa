import React, { useState } from 'react';

/** VOXA Tabs — underline style view switcher. */
export function Tabs({ tabs, active, defaultTab, onChange, style, className = '' }) {
  const [internal, setInternal] = useState(defaultTab ?? tabs[0]);
  const current = active ?? internal;
  const pick = (t) => { setInternal(t); onChange && onChange(t); };
  return (
    <div className={`vx-tabs ${className}`} role="tablist" style={style}>
      {tabs.map((t) => (
        <button key={t} role="tab" aria-selected={current === t} className="vx-tab" onClick={() => pick(t)}>
          {t}
        </button>
      ))}
    </div>
  );
}
