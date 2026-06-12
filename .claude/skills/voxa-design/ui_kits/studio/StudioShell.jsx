// Voxa Studio desktop shell — custom titlebar + icon nav rail + view host.
const { IconButton } = window.VOXADesignSystem_4f47fa;
const ASSET_BASE = '../..';

const STUDIO_NAV = [
  { id: 'talk', icon: 'mic', label: 'Talk' },
  { id: 'playgrounds', icon: 'flask-conical', label: 'Playgrounds' },
  { id: 'builder', icon: 'workflow', label: 'Builder' },
  { id: 'metrics', icon: 'bar-chart-3', label: 'Metrics' },
  { id: 'models', icon: 'box', label: 'Models' },
  { id: 'config', icon: 'settings-2', label: 'Config' },
];

function WinDot({ icon, danger }) {
  return (
    <button
      style={{
        width: 38, height: 30, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        background: 'transparent', border: 'none', color: 'var(--text-3)', cursor: 'default', borderRadius: 6,
        transition: 'background var(--dur-fast) var(--ease-out), color var(--dur-fast) var(--ease-out)',
      }}
      onMouseEnter={(e) => { e.currentTarget.style.background = danger ? 'var(--danger)' : 'var(--surface-3)'; e.currentTarget.style.color = danger ? '#fff' : 'var(--text-1)'; }}
      onMouseLeave={(e) => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = 'var(--text-3)'; }}
    >
      <i data-lucide={icon} style={{ width: 14, height: 14 }}></i>
    </button>
  );
}

function StudioShell({ nav, onNav, live, sessionTime, onReplaySplash, children }) {
  return (
    <div style={{
      height: '100vh', display: 'flex', flexDirection: 'column', overflow: 'hidden',
      background: 'var(--bg-page)', border: '1px solid var(--line-2)',
    }}>
      {/* titlebar */}
      <header style={{
        height: 44, flex: 'none', display: 'flex', alignItems: 'center', gap: 12,
        padding: '0 6px 0 14px', background: 'var(--bg-panel)', borderBottom: '1px solid var(--line-1)',
        userSelect: 'none',
      }}>
        <img src={`${ASSET_BASE}/assets/voxa-mark.svg`} alt="" style={{ height: 18 }} />
        <span style={{ fontFamily: 'var(--font-ui)', fontWeight: 700, fontSize: 12, letterSpacing: '0.28em', color: 'var(--text-1)' }}>
          VOXA <span style={{ fontWeight: 400, color: 'var(--text-3)' }}>STUDIO</span>
        </span>
        <div style={{ flex: 1 }}></div>
        {live && (
          <div className="vx-status vx-status--live" style={{ marginRight: 4 }}>
            <span className="vx-status__dot"></span>
            <span style={{ fontFamily: 'var(--font-mono)' }}>{sessionTime}</span>
          </div>
        )}
        <button onClick={onReplaySplash} title="Replay launch" style={{
          width: 30, height: 30, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          background: 'transparent', border: 'none', color: 'var(--text-3)', cursor: 'pointer', borderRadius: 6,
        }}
          onMouseEnter={(e) => { e.currentTarget.style.color = 'var(--pulse-400)'; }}
          onMouseLeave={(e) => { e.currentTarget.style.color = 'var(--text-3)'; }}>
          <i data-lucide="sparkles" style={{ width: 15, height: 15 }}></i>
        </button>
        <div style={{ width: 1, height: 18, background: 'var(--line-2)', margin: '0 2px' }}></div>
        <WinDot icon="minus" />
        <WinDot icon="square" />
        <WinDot icon="x" danger />
      </header>

      <div style={{ flex: 1, display: 'flex', minHeight: 0 }}>
        {/* nav rail */}
        <nav style={{
          width: 82, flex: 'none', background: 'var(--bg-panel)', borderRight: '1px solid var(--line-1)',
          display: 'flex', flexDirection: 'column', padding: '8px 0', gap: 2,
        }}>
          {STUDIO_NAV.map((n) => {
            const on = nav === n.id;
            return (
              <button key={n.id} onClick={() => onNav(n.id)} title={n.label} style={{
                position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5,
                padding: '11px 4px', margin: '0 6px', background: on ? 'var(--surface-2)' : 'transparent',
                border: '1px solid', borderColor: on ? 'var(--line-1)' : 'transparent', borderRadius: 'var(--r-md)',
                color: on ? 'var(--pulse-400)' : 'var(--text-3)', cursor: 'pointer',
                transition: 'background var(--dur-fast) var(--ease-out), color var(--dur-fast) var(--ease-out)',
              }}
                onMouseEnter={(e) => { if (!on) e.currentTarget.style.color = 'var(--text-1)'; }}
                onMouseLeave={(e) => { if (!on) e.currentTarget.style.color = 'var(--text-3)'; }}>
                {on && <span style={{ position: 'absolute', left: -6, top: 12, bottom: 12, width: 2, borderRadius: 2, background: 'var(--pulse-400)' }}></span>}
                <i data-lucide={n.icon} style={{ width: 20, height: 20 }}></i>
                <span style={{ fontSize: 10, fontWeight: 500, letterSpacing: '0.01em' }}>{n.label}</span>
              </button>
            );
          })}
          <div style={{ marginTop: 'auto', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8, padding: '8px 0' }}>
            <span style={{
              width: 28, height: 28, borderRadius: '50%', background: 'var(--surface-3)', border: '1px solid var(--line-2)',
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-2)',
            }}>MK</span>
          </div>
        </nav>

        {/* view host */}
        <main style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          {children}
        </main>
      </div>
    </div>
  );
}

// Shared view chrome: a toolbar header used by every view for a consistent desktop feel.
function ViewBar({ title, sub, children }) {
  return (
    <header style={{
      flex: 'none', display: 'flex', alignItems: 'center', gap: 14, padding: '12px 20px',
      borderBottom: '1px solid var(--line-1)', background: 'var(--bg-panel)',
    }}>
      <div style={{ minWidth: 0 }}>
        <h1 style={{ fontSize: 18, fontWeight: 700, letterSpacing: '-0.01em', whiteSpace: 'nowrap' }}>{title}</h1>
        {sub && <div className="vx-label" style={{ marginTop: 3 }}>{sub}</div>}
      </div>
      <div style={{ flex: 1 }}></div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>{children}</div>
    </header>
  );
}

window.StudioShell = StudioShell;
window.ViewBar = ViewBar;
