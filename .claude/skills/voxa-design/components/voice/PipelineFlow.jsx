import React from 'react';
import { PipelineNode } from './PipelineNode.jsx';

/** VOXA PipelineFlow — a chain of PipelineNodes with frame-travel links. */
export function PipelineFlow({ nodes, running = false, activeIndex = -1, style, className = '' }) {
  return (
    <div className={`vx-flow ${className}`} style={style}>
      {nodes.map((n, i) => (
        <React.Fragment key={i}>
          {i > 0 && <span className={`vx-flow__link ${running ? 'vx-flow__link--active' : ''}`} />}
          <PipelineNode {...n} active={i === activeIndex} />
        </React.Fragment>
      ))}
    </div>
  );
}
