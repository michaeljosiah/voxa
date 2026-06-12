// Voxa Studio — Run & Metrics workbench (VST-002 §9): percentile card,
// per-turn stage stacks with a plain-language takeaway, run list + compare.
const { Button: MwButton, Select: MwSelect, Card: MwCard, MetricStat: MwMetric } = window.VOXADesignSystem_4f47fa;

const MW_TURNS = [
  { vad: 118, stt: 58, agent: 22, tts: 34, out: 8 },
  { vad: 118, stt: 71, agent: 19, tts: 36, out: 5 },
  { vad: 118, stt: 49, agent: 25, tts: 33, out: 6 },
  { vad: 120, stt: 63, agent: 18, tts: 31, out: 7 },
  { vad: 118, stt: 55, agent: 30, tts: 38, out: 6 },
  { vad: 119, stt: 60, agent: 21, tts: 29, out: 5 },
  { vad: 118, stt: 67, agent: 24, tts: 35, out: 8 },
  { vad: 118, stt: 52, agent: 20, tts: 32, out: 6 },
];
const MW_STAGES = [['vad', 'vad'], ['stt', 'stt'], ['agent', 'agent'], ['tts', 'tts'], ['out', 'out']];
const MW_RUNS = [
  { id: 14, cfg: 'piper', ttfb: 612, on: true },
  { id: 13, cfg: 'kokoro', ttfb: 884, on: true },
  { id: 12, cfg: 'base.en', ttfb: 698, on: false },
];
const MW_MAX = Math.max(...MW_TURNS.map((t) => t.vad + t.stt + t.agent + t.tts + t.out));

function PctCard() {
  return (
    <MwCard style={{ padding: 20, minWidth: 240 }}>
      <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Voice-to-voice TTFB</div>
      <div style={{ fontFamily: 'var(--font-mono)', fontSize: 40, fontWeight: 500, color: 'var(--text-1)', lineHeight: 1.1, marginTop: 12 }}>
        612<span style={{ fontSize: 14, color: 'var(--text-3)', marginLeft: 4 }}>ms p50</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 12, fontFamily: 'var(--font-mono)', fontSize: 14, color: 'var(--text-2)' }}>
        <span>p95&nbsp; 884 ms</span>
        <span>max 1041 ms</span>
      </div>
      <div style={{ marginTop: 16, fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--ok)' }}>▼ 31% vs run #13 (kokoro)</div>
    </MwCard>
  );
}

function StageStacks() {
  return (
    <MwCard style={{ padding: 20, flex: 1, minWidth: 360 }}>
      <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Stage breakdown per turn (ms)</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 7, marginTop: 16 }}>
        {MW_TURNS.map((t, i) => {
          const total = t.vad + t.stt + t.agent + t.tts + t.out;
          return (
            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-muted)', width: 18 }}>{i + 1}</span>
              <div style={{ flex: 1, display: 'flex', height: 13, borderRadius: 3, overflow: 'hidden', background: 'var(--surface-3)' }}>
                {MW_STAGES.map(([s]) => <div key={s} title={`${s} ${t[s]}ms`} style={{ width: `${(t[s] / MW_MAX) * 100}%`, background: `var(--stage-${s})` }}></div>)}
              </div>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-3)', width: 30, textAlign: 'right' }}>{total}</span>
            </div>
          );
        })}
      </div>
      <div style={{ display: 'flex', gap: 16, marginTop: 14, flexWrap: 'wrap' }}>
        {MW_STAGES.map(([s, l]) => (
          <span key={s} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-3)' }}>
            <span style={{ width: 8, height: 8, borderRadius: '50%', background: `var(--stage-${s})` }}></span>{l}
          </span>
        ))}
      </div>
    </MwCard>
  );
}

function RunList() {
  return (
    <MwCard style={{ padding: 18, width: 190, flex: 'none' }}>
      <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Runs</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginTop: 14 }}>
        {MW_RUNS.map((r) => (
          <label key={r.id} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12.5, color: r.on ? 'var(--text-1)' : 'var(--text-3)', cursor: 'pointer' }}>
            <span style={{
              width: 13, height: 13, borderRadius: 3, flex: 'none', border: '1px solid var(--line-3)',
              background: r.on ? 'var(--pulse-400)' : 'transparent',
            }}></span>
            <span style={{ flex: 1 }}>#{r.id} {r.cfg}</span>
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>{r.ttfb}ms</span>
          </label>
        ))}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 16, fontSize: 12 }}>
        <a href="#" onClick={(e) => e.preventDefault()} style={{ color: 'var(--pulse-400)' }}>⇄ compare selected</a>
        <a href="#" onClick={(e) => e.preventDefault()} style={{ color: 'var(--text-3)' }}>⤓ export CSV / JSON</a>
      </div>
    </MwCard>
  );
}

function MetricsWorkbench() {
  return (
    <React.Fragment>
      <ViewBar title="Metrics" sub="run #14 · whispercpp·echo·piper · scripted-8-utterances">
        <MwSelect options={['scripted · 8 utterances', 'live mic', 'jfk.wav fixture']} defaultValue="scripted · 8 utterances" style={{ width: 200 }} />
        <MwButton variant="primary" size="sm" icon={<i data-lucide="play" style={{ width: 14, height: 14 }}></i>}>Run</MwButton>
      </ViewBar>
      <div style={{ flex: 1, overflowY: 'auto', padding: 20 }}>
        <div style={{ display: 'flex', gap: 16, alignItems: 'stretch', flexWrap: 'wrap' }}>
          <PctCard />
          <StageStacks />
          <RunList />
        </div>
        <div style={{
          marginTop: 16, padding: '14px 18px', borderRadius: 'var(--r-lg)', border: '1px solid var(--line-1)',
          borderLeft: '3px solid var(--warn)', background: 'var(--surface-1)', display: 'flex', alignItems: 'center', gap: 10,
        }}>
          <i data-lucide="lightbulb" style={{ width: 16, height: 16, color: 'var(--warn)', flex: 'none' }}></i>
          <span style={{ fontSize: 13, color: 'var(--text-2)' }}><b style={{ color: 'var(--text-1)' }}>Takeaway:</b> VAD hangover is 54% of p50 — lower <code style={{ fontFamily: 'var(--font-mono)', color: 'var(--pulse-300)' }}>StopDuration</code> or enable smart-turn.</span>
        </div>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 16, marginTop: 16 }}>
          <MwCard style={{ padding: 18 }}><MwMetric label="RTF · TTS leg" value="0.28" delta="▼ 0.04 vs #13" deltaTone="good" /></MwCard>
          <MwCard style={{ padding: 18 }}><MwMetric label="Turns" value="8" delta="scripted" deltaTone="flat" /></MwCard>
          <MwCard style={{ padding: 18 }}><MwMetric label="Interruptions" value="2" delta="barge-in" deltaTone="flat" /></MwCard>
          <MwCard style={{ padding: 18 }}><MwMetric label="Errors" value="0" delta="clean run" deltaTone="good" /></MwCard>
        </div>
      </div>
    </React.Fragment>
  );
}

window.MetricsWorkbench = MetricsWorkbench;
