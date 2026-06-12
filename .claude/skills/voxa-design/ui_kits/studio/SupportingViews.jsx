// Voxa Studio — supporting views: Playgrounds wrapper (STT lab | TTS lab),
// Models cache manager, Config composer (v1, gains "open in Builder").
const { Button: SvButton, Badge: SvBadge, Card: SvCard, Select: SvSelect } = window.VOXADesignSystem_4f47fa;

function PlaygroundsView() {
  const [lab, setLab] = React.useState('stt');
  return (
    <React.Fragment>
      <div style={{ flex: 'none', display: 'flex', gap: 4, padding: '8px 14px', borderBottom: '1px solid var(--line-1)', background: 'var(--bg-panel)' }}>
        {[['stt', 'STT lab'], ['tts', 'TTS lab']].map(([id, label]) => (
          <button key={id} onClick={() => setLab(id)} style={{
            padding: '6px 14px', borderRadius: 'var(--r-sm)', border: '1px solid', cursor: 'pointer',
            borderColor: lab === id ? 'var(--line-2)' : 'transparent', background: lab === id ? 'var(--surface-3)' : 'transparent',
            color: lab === id ? 'var(--text-1)' : 'var(--text-3)', fontFamily: 'var(--font-ui)', fontSize: 12.5, fontWeight: 600,
          }}>{label}</button>
        ))}
      </div>
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        {lab === 'stt' ? <window.SttPlayground /> : <window.TtsPlayground />}
      </div>
    </React.Fragment>
  );
}

const MODELS = [
  { name: 'whisper · tiny.en', size: '75 MB', state: 'cached' },
  { name: 'whisper · base.en', size: '142 MB', state: 'cached' },
  { name: 'whisper · small.en', size: '466 MB', state: 'available' },
  { name: 'piper · amy-low', size: '63 MB', state: 'cached' },
  { name: 'kokoro · v0.19', size: '310 MB', state: 'cached' },
  { name: 'silero-vad', size: '2 MB', state: 'cached' },
];

function ModelsView() {
  return (
    <React.Fragment>
      <ViewBar title="Models" sub="local cache · nothing leaves the machine">
        <SvButton variant="secondary" size="sm" icon={<i data-lucide="refresh-cw" style={{ width: 14, height: 14 }}></i>}>Rescan</SvButton>
      </ViewBar>
      <div style={{ flex: 1, overflowY: 'auto', padding: 20 }}>
        <SvCard style={{ padding: 6 }}>
          {MODELS.map((m, i) => (
            <div key={m.name} style={{
              display: 'grid', gridTemplateColumns: '1fr auto auto', alignItems: 'center', gap: 16, padding: '13px 16px',
              borderTop: i ? '1px solid var(--line-1)' : 'none',
            }}>
              <span style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <i data-lucide="box" style={{ width: 16, height: 16, color: 'var(--text-3)' }}></i>
                <span style={{ fontSize: 13.5, color: 'var(--text-1)' }}>{m.name}</span>
              </span>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--text-3)' }}>{m.size}</span>
              {m.state === 'cached'
                ? <SvBadge tone="ok">cached</SvBadge>
                : <SvButton variant="secondary" size="sm" icon={<i data-lucide="download" style={{ width: 13, height: 13 }}></i>}>Download</SvButton>}
            </div>
          ))}
        </SvCard>
      </div>
    </React.Fragment>
  );
}

const CONFIG_STAGES = [
  { stage: 'vad', label: 'Transport', opts: ['WASAPI mic · 16 kHz'] },
  { stage: 'vad', label: 'VAD', opts: ['Silero', 'WebRTC'] },
  { stage: 'stt', label: 'STT', opts: ['WhisperCpp · tiny.en', 'WhisperCpp · base.en'] },
  { stage: 'agent', label: 'Agent', opts: ['OpenAI · gpt-4o-mini', 'OpenAI · gpt-4o'] },
  { stage: 'tts', label: 'TTS', opts: ['Piper · amy-low', 'Kokoro · af_sky'] },
  { stage: 'out', label: 'Sink', opts: ['Default output device'] },
];

function ConfigView({ onOpenBuilder }) {
  return (
    <React.Fragment>
      <ViewBar title="Config" sub="the default composer · appsettings-shaped">
        <SvButton variant="secondary" size="sm" onClick={onOpenBuilder} icon={<i data-lucide="workflow" style={{ width: 14, height: 14 }}></i>}>Open as graph</SvButton>
      </ViewBar>
      <div style={{ flex: 1, overflowY: 'auto', padding: 20 }}>
        <SvCard style={{ padding: 18, maxWidth: 640 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {CONFIG_STAGES.map((s) => (
              <div key={s.label} style={{ display: 'grid', gridTemplateColumns: '120px 1fr', alignItems: 'center', gap: 16 }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 9 }}>
                  <span style={{ width: 9, height: 9, borderRadius: '50%', background: `var(--stage-${s.stage})` }}></span>
                  <span style={{ fontSize: 13, color: 'var(--text-2)' }}>{s.label}</span>
                </span>
                <SvSelect options={s.opts} defaultValue={s.opts[0]} />
              </div>
            ))}
          </div>
        </SvCard>
      </div>
    </React.Fragment>
  );
}

Object.assign(window, { PlaygroundsView, ModelsView, ConfigView });
