// Voxa Studio — Pipeline Builder canvas data + renderers (VST-002 §8).
// Typed ports carry a frame type; colors are the §3.3 stage palette.
const FRAME_COLORS = {
  audio: 'var(--stage-vad)',          // grey — raw/audio frames
  transcription: 'var(--stage-stt)',  // cyan — text from STT
  agentText: 'var(--stage-agent)',    // violet — agent output text
  synthAudio: 'var(--stage-tts)',     // amber — synthesized audio
};
const FRAME_LABEL = { audio: 'audio', transcription: 'transcription', agentText: 'agent-text', synthAudio: 'synth-audio' };

// The default chain the brief draws. Single-in / single-out (the honesty constraint, §8.3).
const CANVAS_W = 1000, CANVAS_H = 300;
const STUDIO_NODES = [
  { id: 'mic',     stage: 'vad',   kind: 'source',   name: 'Mic',        meta: '16 kHz',            x: 20,  y: 118, w: 124, h: 60, inType: null,            outType: 'audio' },
  { id: 'vad',     stage: 'vad',   kind: 'vad',      name: 'Silero VAD', meta: 'stop 800 ms',       x: 188, y: 118, w: 132, h: 60, inType: 'audio',         outType: 'audio' },
  { id: 'stt',     stage: 'stt',   kind: 'stt',      name: 'WhisperCpp', meta: 'tiny.en · cached',  x: 364, y: 118, w: 132, h: 60, inType: 'audio',         outType: 'transcription', cached: true },
  { id: 'agent',   stage: 'agent', kind: 'agent',    name: 'Agent',      meta: 'OpenAI · 4o-mini',  x: 540, y: 118, w: 132, h: 60, inType: 'transcription', outType: 'agentText' },
  { id: 'tts',     stage: 'tts',   kind: 'tts',      name: 'Piper TTS',  meta: 'amy-low · 16 kHz',  x: 716, y: 118, w: 132, h: 60, inType: 'agentText',     outType: 'synthAudio', cached: true },
  { id: 'speaker', stage: 'out',   kind: 'sink',     name: 'Speaker',    meta: 'default device',    x: 892, y: 118, w: 108, h: 60, inType: 'synthAudio',    outType: null },
];
const STUDIO_EDGES = [['mic', 'vad'], ['vad', 'stt'], ['stt', 'agent'], ['agent', 'tts'], ['tts', 'speaker']];

const PALETTE = [
  { kind: 'source', label: 'Source', type: 'audio' },
  { kind: 'vad', label: 'VAD', type: 'audio' },
  { kind: 'stt', label: 'STT provider', type: 'transcription' },
  { kind: 'filter', label: 'TranscriptionFilter', type: 'transcription' },
  { kind: 'agent', label: 'Agent', type: 'agentText' },
  { kind: 'aggregator', label: 'SentenceAggregator', type: 'agentText' },
  { kind: 'tts', label: 'TTS provider', type: 'synthAudio' },
  { kind: 'sink', label: 'Sink', type: 'synthAudio' },
  { kind: 'tap', label: 'DiagnosticsTap', type: 'audio' },
];

function nodeById(id) { return STUDIO_NODES.find((n) => n.id === id); }
function outPort(n) { return { x: n.x + n.w, y: n.y + n.h / 2 }; }
function inPort(n) { return { x: n.x, y: n.y + n.h / 2 }; }

function CanvasEdges({ active }) {
  return (
    <svg width={CANVAS_W} height={CANVAS_H} style={{ position: 'absolute', inset: 0, pointerEvents: 'none', overflow: 'visible' }}>
      <defs>
        <marker id="vx-arr" viewBox="0 0 10 10" refX="8.5" refY="5" markerWidth="7" markerHeight="7" orient="auto">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--pulse-400)" />
        </marker>
      </defs>
      {STUDIO_EDGES.map(([a, b]) => {
        const p1 = outPort(nodeById(a)), p2 = inPort(nodeById(b));
        const mx = (p1.x + p2.x) / 2;
        const d = `M${p1.x},${p1.y} C${mx},${p1.y} ${mx},${p2.y} ${p2.x - 7},${p2.y}`;
        return (
          <g key={a + b}>
            <path d={d} fill="none" stroke="var(--pulse-400)" strokeWidth="2" markerEnd="url(#vx-arr)" opacity={active ? 0.9 : 0.55} />
            {active && (
              <circle r="3.2" fill="var(--pulse-300)">
                <animateMotion dur="1.1s" repeatCount="indefinite" path={d} />
              </circle>
            )}
          </g>
        );
      })}
    </svg>
  );
}

function Port({ type, side }) {
  return (
    <span style={{
      position: 'absolute', top: '50%', [side]: -6, transform: 'translateY(-50%)',
      width: 11, height: 11, borderRadius: '50%', background: FRAME_COLORS[type] || 'var(--ink-400)',
      border: '2px solid var(--bg-page)', boxShadow: '0 0 0 1px var(--line-2)',
    }} title={FRAME_LABEL[type]}></span>
  );
}

function CanvasNode({ node, selected, active, onSelect }) {
  const accent = `var(--stage-${node.stage})`;
  return (
    <div onClick={() => onSelect(node.id)} style={{
      position: 'absolute', left: node.x, top: node.y, width: node.w, height: node.h,
      background: 'var(--surface-2)', borderRadius: 'var(--r-md)', cursor: 'pointer',
      border: '1px solid', borderColor: selected ? accent : 'var(--line-2)',
      borderLeft: `3px solid ${accent}`,
      boxShadow: active ? `0 0 0 1px ${accent}, 0 0 22px -4px ${accent}` : (selected ? `0 0 0 1px ${accent}` : 'var(--shadow-1)'),
      display: 'flex', flexDirection: 'column', justifyContent: 'center', gap: 3, padding: '0 14px',
      transition: 'box-shadow var(--dur-standard) var(--ease-out), border-color var(--dur-fast) var(--ease-out)',
    }}>
      <div style={{ fontFamily: 'var(--font-mono)', fontSize: 9.5, letterSpacing: '0.08em', textTransform: 'uppercase', color: accent }}>{node.kind}</div>
      <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--text-1)' }}>{node.name}</div>
      <div style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-3)', display: 'flex', alignItems: 'center', gap: 5 }}>
        {node.meta}{node.cached && <span style={{ color: 'var(--ok)' }}>✓</span>}
      </div>
      {node.inType && <Port type={node.inType} side="left" />}
      {node.outType && <Port type={node.outType} side="right" />}
    </div>
  );
}

Object.assign(window, { FRAME_COLORS, FRAME_LABEL, CANVAS_W, CANVAS_H, STUDIO_NODES, STUDIO_EDGES, PALETTE, CanvasEdges, CanvasNode });
