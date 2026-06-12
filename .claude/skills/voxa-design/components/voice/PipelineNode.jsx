import React from 'react';

const STAGE_COLORS = {
  vad: 'var(--stage-vad)',
  audio: 'var(--stage-vad)',     // legacy alias — audio frames are grey
  stt: 'var(--stage-stt)',
  agent: 'var(--stage-agent)',
  llm: 'var(--stage-agent)',     // legacy alias
  tts: 'var(--stage-tts)',
  out: 'var(--stage-out)',
};

/** VOXA PipelineNode — one processor in a frame pipeline. */
export function PipelineNode({ stage = 'stt', kind, name, meta, active = false, style, className = '' }) {
  return (
    <div
      className={`vx-node ${active ? 'vx-node--active' : ''} ${className}`}
      style={{ '--node-c': STAGE_COLORS[stage] || STAGE_COLORS.stt, ...style }}
    >
      <span className="vx-node__kind">{kind ?? stage}</span>
      <span className="vx-node__name">{name}</span>
      {meta && <span className="vx-node__meta">{meta}</span>}
    </div>
  );
}
