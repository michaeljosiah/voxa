// Voxa Studio — Pipeline Builder (VST-002 §8): palette → canvas → inspector,
// run-from-canvas with live edge flow + node glow + bottom turn ticker.
const { Button: VxButton, Select: VxSelect, Badge: VxBadge } = window.VOXADesignSystem_4f47fa;

const NODE_PARAMS = {
  mic: [{ k: 'Device', type: 'select', opts: ['Default · WASAPI', 'USB Mic'], v: 'Default · WASAPI' }, { k: 'Sample rate', type: 'select', opts: ['16 kHz', '48 kHz'], v: '16 kHz' }],
  vad: [{ k: 'ConfidenceThreshold', type: 'range', min: 0, max: 1, step: 0.01, v: 0.5 }, { k: 'StopDuration', type: 'range', min: 200, max: 1500, step: 50, v: 800, unit: 'ms' }],
  stt: [{ k: 'Model', type: 'select', opts: ['tiny.en', 'base.en', 'small.en'], v: 'tiny.en' }, { k: 'Language', type: 'select', opts: ['en', 'auto'], v: 'en' }, { k: 'BeamSize', type: 'range', min: 1, max: 8, step: 1, v: 5 }],
  agent: [{ k: 'Model', type: 'select', opts: ['gpt-4o-mini', 'gpt-4o'], v: 'gpt-4o-mini' }, { k: 'Temperature', type: 'range', min: 0, max: 1, step: 0.05, v: 0.7 }],
  tts: [{ k: 'Voice', type: 'select', opts: ['amy-low', 'amy-medium', 'ryan-high'], v: 'amy-low' }, { k: 'Speed', type: 'range', min: 0.5, max: 1.5, step: 0.05, v: 1.0, unit: '×' }],
  speaker: [{ k: 'Device', type: 'select', opts: ['Default output', 'Headphones'], v: 'Default output' }],
};

function Slider({ spec }) {
  const [v, setV] = React.useState(spec.v);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
        <span style={{ fontSize: 12, color: 'var(--text-2)' }}>{spec.k}</span>
        <span style={{ fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--pulse-300)' }}>{v}{spec.unit || ''}</span>
      </div>
      <input className="vxb-range" type="range" min={spec.min} max={spec.max} step={spec.step} value={v}
        onChange={(e) => setV(parseFloat(e.target.value))} />
    </div>
  );
}

function Inspector({ nodeId }) {
  const node = window.STUDIO_NODES.find((n) => n.id === nodeId);
  const specs = NODE_PARAMS[nodeId] || [];
  const accent = `var(--stage-${node.stage})`;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div>
        <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Inspector · {node.kind}</div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8 }}>
          <span style={{ width: 9, height: 9, borderRadius: '50%', background: accent }}></span>
          <span style={{ fontSize: 15, fontWeight: 600 }}>{node.name}</span>
          {node.cached && <VxBadge tone="ok">cached</VxBadge>}
        </div>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        {specs.map((s) => s.type === 'range'
          ? <Slider key={s.k} spec={s} />
          : <VxSelect key={s.k} label={s.k} options={s.opts} defaultValue={s.v} />)}
      </div>
      <div style={{ borderTop: '1px solid var(--line-1)', paddingTop: 14, display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span className="vx-label">Frame types</span>
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>
          in&nbsp; {node.inType ? window.FRAME_LABEL[node.inType] : '—'}<br />
          out {node.outType ? window.FRAME_LABEL[node.outType] : '—'}
        </div>
      </div>
    </div>
  );
}

function TurnTicker() {
  const seg = [['vad', 118], ['stt', 58], ['agent', 22], ['tts', 34], ['out', 8]];
  const total = seg.reduce((a, [, v]) => a + v, 0);
  return (
    <div style={{
      position: 'absolute', left: 16, right: 16, bottom: 12, height: 40, borderRadius: 'var(--r-md)',
      background: 'var(--glass-bg)', backdropFilter: 'var(--blur-glass)', border: '1px solid var(--line-1)',
      display: 'flex', alignItems: 'center', gap: 12, padding: '0 14px',
    }}>
      <span className="vx-label" style={{ flex: 'none' }}>last turn</span>
      <div style={{ flex: 1, display: 'flex', height: 12, borderRadius: 3, overflow: 'hidden' }}>
        {seg.map(([s, v]) => <div key={s} title={`${s} ${v}ms`} style={{ width: `${(v / total) * 100}%`, background: `var(--stage-${s})` }}></div>)}
      </div>
      <span style={{ fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--text-1)' }}>{total} ms</span>
    </div>
  );
}

function PipelineBuilder() {
  const [sel, setSel] = React.useState('vad');
  const [live, setLive] = React.useState(false);
  const [activeIdx, setActiveIdx] = React.useState(0);
  const order = window.STUDIO_NODES.map((n) => n.id);
  React.useEffect(() => {
    if (!live) return;
    const t = setInterval(() => setActiveIdx((i) => (i + 1) % order.length), 520);
    return () => clearInterval(t);
  }, [live]);
  const activeId = live ? order[activeIdx] : null;

  return (
    <React.Fragment>
      <style>{`
        .vxb-range { -webkit-appearance: none; appearance: none; width: 100%; height: 4px; border-radius: 999px;
          background: var(--surface-4); outline: none; }
        .vxb-range::-webkit-slider-thumb { -webkit-appearance: none; width: 15px; height: 15px; border-radius: 50%;
          background: var(--pulse-400); border: 2px solid var(--bg-page); cursor: pointer; box-shadow: 0 0 0 1px var(--pulse-500); }
        .vxb-range::-moz-range-thumb { width: 13px; height: 13px; border-radius: 50%; background: var(--pulse-400); border: 2px solid var(--bg-page); cursor: pointer; }
      `}</style>
      <ViewBar title="Builder" sub="wire the chain · ports accept matching frame types">
        <VxButton variant="secondary" size="sm" icon={<i data-lucide="file-down" style={{ width: 14, height: 14 }}></i>}>appsettings</VxButton>
        <VxButton variant="secondary" size="sm" icon={<i data-lucide="braces" style={{ width: 14, height: 14 }}></i>}>C# compose</VxButton>
        <VxButton variant={live ? 'danger' : 'primary'} size="sm" onClick={() => { setLive((x) => !x); setActiveIdx(0); }}
          icon={<i data-lucide={live ? 'square' : 'play'} style={{ width: 14, height: 14 }}></i>}>
          {live ? 'Stop' : 'Run graph'}
        </VxButton>
      </ViewBar>

      <div style={{ flex: 1, display: 'flex', minHeight: 0 }}>
        {/* palette */}
        <aside style={{ width: 188, flex: 'none', borderRight: '1px solid var(--line-1)', padding: 16, overflowY: 'auto', background: 'var(--bg-panel)' }}>
          <div className="vx-label" style={{ marginBottom: 12 }}>Node palette</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {window.PALETTE.map((p) => (
              <div key={p.label} style={{
                display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px', borderRadius: 'var(--r-sm)',
                border: '1px solid var(--line-1)', background: 'var(--surface-2)', cursor: 'grab', fontSize: 12, color: 'var(--text-2)',
              }}>
                <span style={{ width: 8, height: 8, borderRadius: '50%', flex: 'none', background: window.FRAME_COLORS[p.type] }}></span>
                {p.label}
              </div>
            ))}
          </div>
          <p style={{ marginTop: 14, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>Drag onto the canvas, or use a port's <b style={{ color: 'var(--text-3)' }}>+</b> to add only type-compatible nodes.</p>
        </aside>

        {/* canvas */}
        <div style={{ flex: 1, position: 'relative', overflow: 'auto', background: 'var(--bg-page)' }}>
          <div style={{
            position: 'relative', width: window.CANVAS_W, height: window.CANVAS_H, margin: '28px 24px',
            backgroundImage: 'linear-gradient(var(--line-1) 1px, transparent 1px), linear-gradient(90deg, var(--line-1) 1px, transparent 1px)',
            backgroundSize: '40px 40px',
          }}>
            <div className="vx-label" style={{ position: 'absolute', top: -22, left: 0 }}>Builder canvas · single-in / single-out</div>
            <window.CanvasEdges active={live} />
            {window.STUDIO_NODES.map((n) => (
              <window.CanvasNode key={n.id} node={n} selected={sel === n.id} active={activeId === n.id} onSelect={setSel} />
            ))}
            {live && <TurnTicker />}
          </div>
        </div>

        {/* inspector */}
        <aside style={{ width: 264, flex: 'none', borderLeft: '1px solid var(--line-1)', padding: 18, overflowY: 'auto', background: 'var(--bg-panel)' }}>
          <Inspector nodeId={sel} />
        </aside>
      </div>
    </React.Fragment>
  );
}

window.PipelineBuilder = PipelineBuilder;
