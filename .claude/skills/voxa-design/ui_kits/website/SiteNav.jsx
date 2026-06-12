const { Button, Badge } = window.VOXADesignSystem_4f47fa;

function SiteNav() {
  return (
    <nav style={{
      position: 'sticky', top: 0, zIndex: 10,
      display: 'flex', alignItems: 'center', gap: 28,
      padding: '14px 40px', borderBottom: '1px solid var(--line-1)',
      background: 'var(--glass-bg)', backdropFilter: 'var(--blur-glass)',
    }}>
      <img src={(window.DS_BASE || '../..') + '/assets/voxa-logo.svg'} alt="VOXA" style={{ height: 28 }} />
      <Badge tone="pulse">v0.4 preview</Badge>
      <div style={{ flex: 1 }} />
      <div style={{ display: 'flex', alignItems: 'center', gap: 22 }}>
        <a href="#" style={{ fontSize: 13, fontWeight: 500, color: 'var(--text-2)' }}>Docs</a>
        <a href="#" style={{ fontSize: 13, fontWeight: 500, color: 'var(--text-2)' }}>Examples</a>
        <a href="#" style={{ fontSize: 13, fontWeight: 500, color: 'var(--text-2)' }}>GitHub</a>
        <Button variant="primary" size="sm">Get started</Button>
      </div>
    </nav>
  );
}

window.SiteNav = SiteNav;
