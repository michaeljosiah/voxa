// Voxa Studio — STT Playground (VST-002 §6): how well & how fast does speech
// become text on this machine, for any Whisper model, without a whole pipeline.
const { Button: StButton, Select: StSelect, Badge: StBadge, Switch: StSwitch, Waveform: StWave } = window.VOXADesignSystem_4f47fa;

const STT_SOURCES = [
  { id: 'mic', icon: 'mic', label: 'Live mic' },
  { id: 'file', icon: 'file-audio', label: 'Drop WAV' },
  { id: 'fixture', icon: 'repeat', label: 'Fixtures' },
];
const STT_CARDS = [
  { text: "What's the status of order four four two one?", dur: '2.1s', latency: 142, levels: [.3, .6, .8, .5, .9, .7, .4, .6, .3, .7, .5, .2, .6, .8, .4] },
  { text: 'Can you ship it express instead?', dur: '1.6s', latency: 118, levels: [.4, .7, .5, .9, .6, .3, .7, .5, .8, .4, .6, .3] },
];
// WER diff: reference vs hypothesis token ops.
const WER_DIFF = [
  { w: 'the', op: 'ok' }, { w: 'quick', op: 'ok' }, { w: 'brown', op: 'ok' }, { w: 'fox', op: 'ok' },
  { w: 'jumped', op: 'sub', was: 'jumps' }, { w: 'over', op: 'ok' }, { w: 'a', op: 'sub', was: 'the' },
  { w: 'lazy', op: 'ok' }, { w: 'dog', op: 'ok' }, { w: 'today', op: 'ins' },
];

function SttPlayground() {
  const [src, setSrc] = React.useState('fixture');
  const [side, setSide] = React.useState(false);
  return (
    <React.Fragment>
      <ViewBar title="STT Playground" sub="speech → text · standalone, measured on this machine">
        <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span className="vx-label">side-by-side</span>
          <StSwitch checked={side} onChange={setSide} />
        </span>
      </ViewBar>
      <div style={{ flex: 1, overflowY: 'auto', padding: 20, display: 'flex', flexDirection: 'column', gap: 16 }}>
        {/* input strip */}
        <div style={{
          display: 'flex', alignItems: 'center', gap: 16, padding: 14, borderRadius: 'var(--r-lg)',
          border: '1px solid var(--line-1)', background: 'var(--surface-1)', flexWrap: 'wrap',
        }}>
          <div style={{ display: 'flex', gap: 4, padding: 3, borderRadius: 'var(--r-md)', background: 'var(--surface-3)' }}>
            {STT_SOURCES.map((s) => (
              <button key={s.id} onClick={() => setSrc(s.id)} style={{
                display: 'inline-flex', alignItems: 'center', gap: 7, padding: '7px 12px', borderRadius: 'var(--r-sm)', border: 'none', cursor: 'pointer',
                background: src === s.id ? 'var(--pulse-400)' : 'transparent', color: src === s.id ? 'var(--text-on-pulse)' : 'var(--text-2)',
                fontFamily: 'var(--font-ui)', fontSize: 12.5, fontWeight: 600,
              }}>
                <i data-lucide={s.icon} style={{ width: 14, height: 14 }}></i>{s.label}
              </button>
            ))}
          </div>
          <StSelect options={['tiny.en', 'base.en', 'small.en', 'tiny.en · q5_1']} defaultValue="tiny.en" style={{ width: 150 }} />
          <StBadge tone="neutral">75 MB</StBadge>
          <StBadge tone="ok">cached</StBadge>
          <div style={{ flex: 1 }}></div>
          <StButton variant="primary" size="sm" icon={<i data-lucide="play" style={{ width: 14, height: 14 }}></i>}>Transcribe jfk.wav</StButton>
        </div>

        <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start', flexWrap: 'wrap' }}>
          {/* transcript pane */}
          <div style={{ flex: 1, minWidth: 320, display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div className="vx-label">Transcript · final cards</div>
            {STT_CARDS.map((c, i) => (
              <div key={i} style={{ padding: 14, borderRadius: 'var(--r-lg)', border: '1px solid var(--line-1)', background: 'var(--surface-card)', boxShadow: 'var(--shadow-1), var(--edge-light)' }}>
                <StWave bars={c.levels.length} levels={c.levels} height={26} style={{ marginBottom: 10 }} />
                <div style={{ fontSize: 14, color: 'var(--text-1)' }}>{c.text}</div>
                <div style={{ display: 'flex', gap: 14, marginTop: 8, fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>
                  <span>utterance {c.dur}</span>
                  <span style={{ color: 'var(--pulse-300)' }}>final +{c.latency} ms</span>
                </div>
              </div>
            ))}
            {side && (
              <div style={{ padding: 14, borderRadius: 'var(--r-lg)', border: '1px dashed var(--line-2)', color: 'var(--text-3)', fontSize: 12.5 }}>
                base.en · same audio → <b style={{ color: 'var(--text-2)' }}>4.9% WER</b>, 240 ms slower. One whisper context at a time.
              </div>
            )}
          </div>

          {/* accuracy harness */}
          <div style={{ width: 340, flex: 'none', padding: 16, borderRadius: 'var(--r-lg)', border: '1px solid var(--line-1)', background: 'var(--surface-1)' }}>
            <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Accuracy harness · WER</div>
            <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, margin: '12px 0 14px' }}>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 34, fontWeight: 500, color: 'var(--text-1)' }}>8.1<span style={{ fontSize: 15, color: 'var(--text-3)' }}>%</span></span>
              <span style={{ fontSize: 12, color: 'var(--text-3)' }}>1 sub · 1 ins · 0 del</span>
            </div>
            <div style={{ lineHeight: 1.9, fontSize: 13.5 }}>
              {WER_DIFF.map((t, i) => {
                const st = t.op === 'ok'
                  ? { color: 'var(--text-2)' }
                  : t.op === 'sub' ? { color: 'var(--warn)', background: 'var(--warn-soft)', borderRadius: 3, padding: '1px 4px' }
                  : { color: 'var(--info)', background: 'var(--info-soft)', borderRadius: 3, padding: '1px 4px' };
                return <span key={i}><span style={st} title={t.was ? `was "${t.was}"` : t.op === 'ins' ? 'inserted' : ''}>{t.w}</span>{' '}</span>;
              })}
            </div>
            <div style={{ display: 'flex', gap: 14, marginTop: 16, fontSize: 11, color: 'var(--text-muted)' }}>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}><span style={{ width: 8, height: 8, borderRadius: 2, background: 'var(--warn)' }}></span>substitution</span>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}><span style={{ width: 8, height: 8, borderRadius: 2, background: 'var(--info)' }}></span>insertion</span>
            </div>
          </div>
        </div>
      </div>
    </React.Fragment>
  );
}

window.SttPlayground = SttPlayground;
