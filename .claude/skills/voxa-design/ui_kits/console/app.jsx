const { Button, StatusPill } = window.VOXADesignSystem_4f47fa;

const SESSIONS = [
  { id: 'sx_4f81a2', pipeline: 'support-voice', status: 'live', duration: '04:12', region: 'westus2' },
  { id: 'sx_4f7b9c', pipeline: 'support-voice', status: 'live', duration: '01:48', region: 'westus2' },
  { id: 'sx_4f7902', pipeline: 'outbound-reminders', status: 'connecting', duration: '00:03', region: 'eastus' },
  { id: 'sx_4f76e1', pipeline: 'support-voice', status: 'ended', duration: '06:31', region: 'westus2' },
  { id: 'sx_4f74d0', pipeline: 'kiosk-concierge', status: 'ended', duration: '02:54', region: 'swedencentral' },
  { id: 'sx_4f71b8', pipeline: 'support-voice', status: 'error', duration: '00:41', region: 'eastus' },
];

function App() {
  const [nav, setNav] = React.useState('sessions');
  const [open, setOpen] = React.useState(null);
  const [tick, setTick] = React.useState(0);

  React.useEffect(() => {
    if (!open) return;
    const t = setInterval(() => setTick((x) => x + 1), 1800);
    return () => clearInterval(t);
  }, [open]);

  React.useEffect(() => { window.lucide && lucide.createIcons({ attrs: { 'stroke-width': 1.75 } }); });

  const title = open ? `Session ${open.id}` : nav === 'sessions' ? 'Sessions' : 'Sessions';
  return (
    <ConsoleShell
      nav={nav}
      onNav={(id) => { setNav('sessions'); setOpen(null); }}
      title={title}
      actions={open
        ? <Button variant="secondary" onClick={() => setOpen(null)}>← All sessions</Button>
        : <Button variant="primary">New pipeline</Button>}
    >
      {open
        ? <LiveSessionView session={open} tick={tick} />
        : <SessionsView sessions={SESSIONS} onOpen={(s) => { setOpen(s); setTick(0); }} />}
    </ConsoleShell>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
