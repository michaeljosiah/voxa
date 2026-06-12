// Voxa Studio — app shell: splash → desktop shell, view routing, session clock.
function fmt(s) { return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; }

function StudioApp() {
  const [splash, setSplash] = React.useState(true);
  const [nav, setNav] = React.useState('builder');
  const [clock, setClock] = React.useState(21);

  // splash auto-dismisses once init "completes"
  React.useEffect(() => {
    if (!splash) return;
    const t = setTimeout(() => setSplash(false), 2300);
    return () => clearTimeout(t);
  }, [splash]);

  const live = nav === 'talk';
  React.useEffect(() => {
    if (!live) return;
    const t = setInterval(() => setClock((c) => c + 1), 1000);
    return () => clearInterval(t);
  }, [live]);

  // refresh icons after every render (views inject new <i data-lucide>)
  React.useEffect(() => { window.renderStudioIcons && window.renderStudioIcons(); });

  const view = {
    talk: <window.TalkView />,
    playgrounds: <window.PlaygroundsView />,
    builder: <window.PipelineBuilder />,
    metrics: <window.MetricsWorkbench />,
    models: <window.ModelsView />,
    config: <window.ConfigView onOpenBuilder={() => setNav('builder')} />,
  }[nav];

  return (
    <React.Fragment>
      <StudioShell nav={nav} onNav={setNav} live={live} sessionTime={fmt(clock)} onReplaySplash={() => setSplash(true)}>
        {view}
      </StudioShell>
      {splash && (
        <div onClick={() => setSplash(false)} style={{
          position: 'fixed', inset: 0, zIndex: 50, display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: 'rgba(5, 7, 10, 0.82)', backdropFilter: 'blur(6px)',
        }}>
          <Splash onSkip={() => setSplash(false)} />
        </div>
      )}
    </React.Fragment>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<StudioApp />);
