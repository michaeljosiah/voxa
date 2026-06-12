const { Button, Badge, CodeBlock, VoiceOrb, Waveform } = window.VOXADesignSystem_4f47fa;

const SAMPLE = `var pipeline = new VoxaPipeline()
    .Use<WebRtcTransport>()
    .Use<AzureSpeechStt>()
    .Use<AgentTurn>("support-agent")   // Microsoft Agent Framework
    .Use<AzureNeuralTts>("en-US-Ava");

await pipeline.RunAsync();`;

function Hero() {
  return (
    <section style={{ position: 'relative', padding: '88px 40px 72px', overflow: 'hidden' }}>
      <div style={{
        position: 'absolute', inset: 0, pointerEvents: 'none',
        background: 'radial-gradient(700px 380px at 72% 30%, rgba(79,195,247,0.10), transparent 70%)',
      }} />
      <div style={{ position: 'relative', maxWidth: 'var(--container-max)', margin: '0 auto', display: 'grid', gridTemplateColumns: '1.05fr 1fr', gap: 56, alignItems: 'center' }}>
        <div>
          <span className="vx-label" style={{ color: 'var(--pulse-300)' }}>frame-based · real-time · .NET</span>
          <h1 style={{ fontSize: 'var(--fs-5xl)', marginTop: 14 }}>Voice agents,<br />frame by frame.</h1>
          <p style={{ fontSize: 'var(--fs-lg)', color: 'var(--text-2)', marginTop: 18, maxWidth: '46ch' }}>
            VOXA is a real-time voice pipeline framework for .NET. Compose speech, reasoning and synthesis
            as frame processors — built on Microsoft Agent Framework and Azure.
          </p>
          <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginTop: 28 }}>
            <Button variant="primary" size="lg">Get started</Button>
            <code style={{
              display: 'inline-flex', alignItems: 'center', gap: 10, height: 46, padding: '0 18px',
              background: 'var(--surface-2)', border: '1px solid var(--line-2)', borderRadius: 'var(--r-md)',
              fontSize: 13, color: 'var(--text-2)',
            }}>
              <span style={{ color: 'var(--pulse-400)' }}>$</span> dotnet add package Voxa
            </code>
          </div>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 18, alignItems: 'center' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 22 }}>
            <VoiceOrb size={104} live />
            <Waveform live gradient bars={18} height={40} />
          </div>
          <CodeBlock file="Program.cs" badge="C#" code={SAMPLE} style={{ width: '100%' }} />
        </div>
      </div>
    </section>
  );
}

window.Hero = Hero;
