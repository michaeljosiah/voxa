function Footer() {
  return (
    <footer style={{ borderTop: '1px solid var(--line-1)', padding: '28px 40px' }}>
      <div style={{ maxWidth: 'var(--container-max)', margin: '0 auto', display: 'flex', alignItems: 'center', gap: 24 }}>
        <img src={(window.DS_BASE || '../..') + '/assets/voxa-wordmark-mono.svg'} alt="VOXA" style={{ height: 16, opacity: 0.7 }} />
        <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>© 2026 Voxa Labs</span>
        <div style={{ flex: 1 }} />
        <div style={{ display: 'flex', gap: 20 }}>
          <a href="#" style={{ fontSize: 12, color: 'var(--text-3)' }}>Docs</a>
          <a href="#" style={{ fontSize: 12, color: 'var(--text-3)' }}>NuGet</a>
          <a href="#" style={{ fontSize: 12, color: 'var(--text-3)' }}>GitHub</a>
          <a href="#" style={{ fontSize: 12, color: 'var(--text-3)' }}>Privacy</a>
        </div>
      </div>
    </footer>
  );
}

window.Footer = Footer;
