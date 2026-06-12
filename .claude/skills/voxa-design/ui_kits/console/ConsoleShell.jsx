const { IconButton, Badge, StatusPill } = window.VOXADesignSystem_4f47fa;

const NAV = [
  { id: 'sessions', icon: 'activity', label: 'Sessions' },
  { id: 'pipelines', icon: 'workflow', label: 'Pipelines' },
  { id: 'voices', icon: 'audio-lines', label: 'Voices' },
  { id: 'keys', icon: 'key-round', label: 'Keys' },
  { id: 'settings', icon: 'settings-2', label: 'Settings' },
];

function ConsoleShell({ nav, onNav, title, actions, children }) {
  return (
    <div style={{ display: 'flex', height: '100vh', overflow: 'hidden' }}>
      <aside style={{
        width: 'var(--sidebar-w)', flex: 'none', background: 'var(--bg-panel)',
        borderRight: '1px solid var(--line-1)', display: 'flex', flexDirection: 'column', padding: '18px 12px',
      }}>
        <img src={(window.DS_BASE || '../..') + '/assets/voxa-logo.svg'} alt="VOXA" style={{ height: 30, alignSelf: 'flex-start', margin: '2px 6px 22px' }} />
        <nav style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          {NAV.map((n) => (
            <button key={n.id} onClick={() => onNav(n.id)} style={{
              display: 'flex', alignItems: 'center', gap: 10, padding: '9px 10px',
              background: nav === n.id ? 'var(--surface-3)' : 'transparent',
              border: '1px solid', borderColor: nav === n.id ? 'var(--line-1)' : 'transparent',
              borderRadius: 'var(--r-md)', cursor: 'pointer',
              color: nav === n.id ? 'var(--text-1)' : 'var(--text-3)',
              font: '500 13px var(--font-body)', textAlign: 'left',
              transition: 'background var(--dur-1) var(--ease-out), color var(--dur-1) var(--ease-out)',
            }}>
              <i data-lucide={n.icon} style={{ width: 16, height: 16 }}></i>
              {n.label}
            </button>
          ))}
        </nav>
        <div style={{ marginTop: 'auto', display: 'flex', flexDirection: 'column', gap: 12, padding: '0 6px' }}>
          <Badge tone="neutral">westus2 · prod</Badge>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <span style={{
              width: 28, height: 28, borderRadius: '50%', flex: 'none',
              background: 'var(--surface-4)', border: '1px solid var(--line-2)',
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
              font: '600 11px var(--font-mono)', color: 'var(--text-2)',
            }}>MK</span>
            <span style={{ fontSize: 12, color: 'var(--text-3)' }}>m.kovac@contoso.dev</span>
          </div>
        </div>
      </aside>
      <main style={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <header style={{
          display: 'flex', alignItems: 'center', gap: 16, padding: '14px 24px',
          borderBottom: '1px solid var(--line-1)', background: 'var(--glass-bg)', backdropFilter: 'var(--blur-glass)',
        }}>
          <h1 style={{ fontSize: 20, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{title}</h1>
          {actions}
        </header>
        <div style={{ flex: 1, overflowY: 'auto', padding: 24 }}>{children}</div>
      </main>
    </div>
  );
}

window.ConsoleShell = ConsoleShell;
