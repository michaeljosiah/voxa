const { Card, Button, Tabs, StatusPill, Waveform, MetricStat, PipelineFlow, TranscriptLine, Switch, Select } = window.VOXADesignSystem_4f47fa;

const TRANSCRIPT = [
  { role: 'user', text: "Hi — what's the status of my order?", time: '00:08.120' },
  { role: 'agent', text: 'Let me check that for you. Can you confirm the order number?', time: '00:09.480' },
  { role: 'user', text: 'It should be 4-4-2-1.', time: '00:14.900' },
  { role: 'system', text: 'tool: Orders.Lookup("4421") → shipped', time: '00:15.310' },
  { role: 'agent', text: 'Order 4421 shipped this morning via FedEx —', time: '00:16.040' },
  { role: 'user', text: 'Oh wait, sorry —', time: '00:17.610' },
  { role: 'system', text: 'frame: interruption · agent yielded in 96 ms', time: '00:17.702' },
];

const FRAMES = [
  { role: 'system', text: 'AudioRawFrame · 320 bytes · 16 kHz', time: '00:17.690' },
  { role: 'system', text: 'UserStartedSpeakingFrame', time: '00:17.698' },
  { role: 'system', text: 'StartInterruptionFrame → downstream', time: '00:17.702' },
  { role: 'system', text: 'TTSStoppedFrame · AzureNeuralTts', time: '00:17.745' },
  { role: 'system', text: 'TranscriptionFrame · "Oh wait, sorry —"', time: '00:18.230' },
];

function LiveSessionView({ session, tick }) {
  const [tab, setTab] = React.useState('Transcript');
  const visible = TRANSCRIPT.slice(0, Math.min(2 + tick, TRANSCRIPT.length));
  const partial = tick % 6 === 5;
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 300px', gap: 24, maxWidth: 1080, alignItems: 'start' }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 20, minWidth: 0 }}>
        <Card padded style={{ overflowX: 'auto' }}>
          <span className="vx-label" style={{ display: 'block', marginBottom: 12 }}>Pipeline · support-voice</span>
          <PipelineFlow running activeIndex={(tick % 4)} nodes={[
            { stage: 'audio', name: 'WebRtcTransport', meta: '48 kHz · opus' },
            { stage: 'stt', name: 'AzureSpeechStt', meta: 'p50 118 ms' },
            { stage: 'llm', name: 'AgentTurn', meta: 'gpt-4o · MAF' },
            { stage: 'tts', name: 'AzureNeuralTts', meta: 'en-US-Ava' },
          ]} />
        </Card>

        <Card padded={false}>
          <div style={{ padding: '4px 20px 0' }}>
            <Tabs tabs={['Transcript', 'Frames', 'Metrics']} active={tab} onChange={setTab} />
          </div>
          <div style={{ padding: '6px 20px 14px', minHeight: 260 }}>
            {tab === 'Transcript' && (
              <div>
                {visible.map((l, i) => <TranscriptLine key={i} {...l} />)}
                {partial && <TranscriptLine role="agent" text="No problem — take your" partial />}
              </div>
            )}
            {tab === 'Frames' && (
              <div>
                {FRAMES.map((l, i) => <TranscriptLine key={i} {...l} />)}
              </div>
            )}
            {tab === 'Metrics' && (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 24, padding: '18px 0' }}>
                <MetricStat label="TTFB" value="388" unit="ms" delta="▼ 24 ms vs p50" deltaTone="good" />
                <MetricStat label="STT latency" value="118" unit="ms" delta="— stable" deltaTone="flat" />
                <MetricStat label="Turns" value="14" delta="▲ streaming" deltaTone="good" />
              </div>
            )}
          </div>
        </Card>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <Card glow>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
            <StatusPill status="live" />
            <code style={{ fontSize: 11, color: 'var(--text-3)' }}>{session.duration}</code>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
            <Waveform live gradient bars={14} height={30} />
            <code style={{ fontSize: 11, color: 'var(--text-3)' }}>en-US-Ava</code>
          </div>
        </Card>
        <Card>
          <span className="vx-label" style={{ display: 'block', marginBottom: 14 }}>Session settings</span>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <Switch label="Interruptions" defaultChecked />
            <Switch label="Noise suppression" defaultChecked />
            <Switch label="Record audio" />
            <Select label="Voice" options={['en-US-Ava', 'en-US-Andrew', 'en-GB-Sonia']} />
          </div>
        </Card>
        <Button variant="danger">End session</Button>
      </div>
    </div>
  );
}

window.LiveSessionView = LiveSessionView;
