const { Card, Badge, StatusPill, Waveform, MetricStat, Button } = window.VOXADesignSystem_4f47fa;

function SessionsView({ sessions, onOpen }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 24, maxWidth: 980 }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 16 }}>
        <Card><MetricStat label="Active sessions" value="23" delta="▲ 4 last hour" deltaTone="good" /></Card>
        <Card><MetricStat label="TTFB p50" value="412" unit="ms" delta="▼ 38 ms" deltaTone="good" /></Card>
        <Card><MetricStat label="Interruptions" value="8.2" unit="%" delta="— flat" deltaTone="flat" /></Card>
        <Card><MetricStat label="Error rate" value="0.4" unit="%" delta="▲ 0.1 pt" deltaTone="bad" /></Card>
      </div>

      <div>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 12, marginBottom: 12 }}>
          <h2 style={{ fontSize: 16, fontWeight: 600 }}>Sessions</h2>
          <span className="vx-label">today · 1,284 total</span>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {sessions.map((s) => (
            <Card key={s.id} interactive padded={false} onClick={() => onOpen(s)}
              style={{ display: 'grid', gridTemplateColumns: '110px 1fr 120px 110px 90px 90px', alignItems: 'center', gap: 16, padding: '12px 16px' }}>
              <code style={{ fontSize: 12, color: 'var(--text-2)' }}>{s.id}</code>
              <span style={{ fontSize: 13, fontWeight: 500 }}>{s.pipeline}</span>
              <StatusPill status={s.status} />
              {s.status === 'live'
                ? <Waveform live bars={9} height={16} />
                : <Waveform muted bars={9} height={16} />}
              <code style={{ fontSize: 12, color: 'var(--text-3)' }}>{s.duration}</code>
              <Badge tone="neutral">{s.region}</Badge>
            </Card>
          ))}
        </div>
      </div>
    </div>
  );
}

window.SessionsView = SessionsView;
