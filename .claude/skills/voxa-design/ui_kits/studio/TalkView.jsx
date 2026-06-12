// Voxa Studio — Talk (the v1 conversation view; role unchanged). Watch the
// pipeline think: orb + live waveform, streaming transcript, running chain.
const { TranscriptLine: TkLine, VoiceOrb: TkOrb, Waveform: TkWave, PipelineFlow: TkFlow, StatusPill: TkPill, Button: TkButton } = window.VOXADesignSystem_4f47fa;

const TALK_LINES = [
  { role: 'agent', text: 'Order 4421 shipped this morning via FedEx —', time: '00:16.040' },
  { role: 'user', text: 'Oh wait, sorry —', time: '00:17.610' },
  { role: 'system', text: 'frame: interruption · agent yielded in 96 ms', time: '00:17.702' },
  { role: 'user', text: 'can you send it to my office address instead?', time: '00:19.180' },
  { role: 'agent', text: 'Sure — updating the shipment to your office now.', time: '00:20.450', partial: true },
];
const TALK_CHAIN = [
  { stage: 'vad', kind: 'source', name: 'Mic', meta: '16 kHz' },
  { stage: 'vad', kind: 'vad', name: 'Silero', meta: 'gate' },
  { stage: 'stt', kind: 'stt', name: 'Whisper', meta: 'tiny.en' },
  { stage: 'agent', kind: 'agent', name: 'Agent', meta: '4o-mini' },
  { stage: 'tts', kind: 'tts', name: 'Piper', meta: 'amy-low' },
  { stage: 'out', kind: 'sink', name: 'Speaker', meta: 'live' },
];

function TalkView() {
  const [active, setActive] = React.useState(2);
  React.useEffect(() => {
    const t = setInterval(() => setActive((i) => (i + 1) % TALK_CHAIN.length), 600);
    return () => clearInterval(t);
  }, []);
  return (
    <React.Fragment>
      <ViewBar title="Talk" sub="live conversation · watch it think">
        <TkPill status="live" label="00:21" />
        <TkButton variant="danger" size="sm" icon={<i data-lucide="phone-off" style={{ width: 14, height: 14 }}></i>}>End session</TkButton>
      </ViewBar>
      <div style={{ flex: 1, display: 'flex', minHeight: 0 }}>
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <div style={{ flex: 1, overflowY: 'auto', padding: '20px 28px', display: 'flex', flexDirection: 'column' }}>
            {TALK_LINES.map((l, i) => <TkLine key={i} {...l} />)}
          </div>
          <div style={{ flex: 'none', padding: '14px 28px', borderTop: '1px solid var(--line-1)', background: 'var(--bg-panel)' }}>
            <TkFlow nodes={TALK_CHAIN} running activeIndex={active} />
          </div>
        </div>
        <aside style={{ width: 280, flex: 'none', borderLeft: '1px solid var(--line-1)', background: 'var(--bg-panel)', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 18, padding: '36px 24px' }}>
          <TkOrb size={132} live />
          <TkWave bars={20} height={32} live />
          <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>agent speaking</div>
          <div style={{ marginTop: 'auto', width: '100%', display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--text-2)' }}><span style={{ color: 'var(--text-3)' }}>TTFB</span><span>118 ms</span></div>
            <div style={{ display: 'flex', justifyContent: 'space-between', fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--text-2)' }}><span style={{ color: 'var(--text-3)' }}>turns</span><span>4</span></div>
            <div style={{ display: 'flex', justifyContent: 'space-between', fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--text-2)' }}><span style={{ color: 'var(--text-3)' }}>barge-in</span><span>1</span></div>
          </div>
        </aside>
      </div>
    </React.Fragment>
  );
}

window.TalkView = TalkView;
