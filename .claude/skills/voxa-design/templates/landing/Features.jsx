const { Card, PipelineFlow } = window.VOXADesignSystem_4f47fa;

const FEATURES = [
  {
    icon: 'workflow',
    title: 'Frames all the way down',
    body: 'Audio, transcription, LLM tokens and control signals are all frames in one ordered stream — interruptions and barge-in fall out of the model for free.',
  },
  {
    icon: 'bot',
    title: 'Agent Framework native',
    body: 'Drop a Microsoft Agent Framework agent into the pipeline as a processor. Tools, memory and handoffs work mid-conversation.',
  },
  {
    icon: 'cloud',
    title: 'Azure end to end',
    body: 'First-class processors for Azure Speech, Azure OpenAI and ACS telephony. Deploy as a container app; scale per session.',
  },
];

function Features() {
  return (
    <section style={{ padding: '0 40px 80px' }}>
      <div style={{ maxWidth: 'var(--container-max)', margin: '0 auto' }}>
        <div style={{
          border: '1px solid var(--line-1)', borderRadius: 'var(--r-xl)',
          background: 'var(--bg-panel)', padding: '28px 28px 24px', overflowX: 'auto',
        }}>
          <span className="vx-label" style={{ display: 'block', marginBottom: 16 }}>One pipeline · four stages · &lt;500 ms round trip</span>
          <PipelineFlow running nodes={[
            { stage: 'audio', name: 'WebRtcTransport', meta: '48 kHz · opus' },
            { stage: 'stt', name: 'AzureSpeechStt', meta: 'streaming' },
            { stage: 'llm', name: 'AgentTurn', meta: 'Agent Framework' },
            { stage: 'tts', name: 'AzureNeuralTts', meta: 'neural voices' },
          ]} />
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 18, marginTop: 18 }}>
          {FEATURES.map((f) => (
            <Card key={f.title} style={{ padding: 24 }}>
              <i data-lucide={f.icon} style={{ width: 22, height: 22, color: 'var(--pulse-400)' }}></i>
              <h3 style={{ fontSize: 17, fontWeight: 600, marginTop: 14 }}>{f.title}</h3>
              <p style={{ fontSize: 13, color: 'var(--text-2)', marginTop: 8 }}>{f.body}</p>
            </Card>
          ))}
        </div>
      </div>
    </section>
  );
}

window.Features = Features;
