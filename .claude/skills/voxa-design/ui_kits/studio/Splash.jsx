// Voxa Studio — animated launch splash (480×320 borderless window per VST-002 §4).
// Mark draws on, three signal bars spring up, wordmark settles, microcopy ticks.
// Honors prefers-reduced-motion. Click to skip.
//
// Static-capture safe: the visible END state is set as INLINE styles (always
// captured by static renderers); the intro animation is layered via the
// `.vxs-play` class, which overrides during playback and is removed when done.
const SPLASH_STAGES = ['configuration', 'providers', 'devices', 'model cache', 'ready'];

function SplashStyles() {
  return (
    <style>{`
      .vxs-stage { position: relative; width: 480px; height: 320px; border-radius: 14px; overflow: hidden;
        background: radial-gradient(ellipse 90% 70% at 50% 42%, #11161D 0%, #0B0F14 72%);
        border: 1px solid var(--line-2); box-shadow: 0 30px 90px rgba(0,0,0,0.6);
        display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 22px; cursor: pointer; }
      .vxs-mark { width: 88px; height: 88px; overflow: visible; }
      .vxs-play .v { animation: vxs-draw 1.1s cubic-bezier(.6,0,.2,1) backwards; }
      .vxs-play .bar { animation: vxs-bar .5s cubic-bezier(.2,.8,.2,1.3) backwards; }
      .vxs-play .bar.b1 { animation-delay: .9s; } .vxs-play .bar.b2 { animation-delay: 1.0s; } .vxs-play .bar.b3 { animation-delay: 1.1s; }
      .vxs-play .glow { animation: vxs-glow 2.6s ease-in-out 1.5s infinite; }
      .vxs-play .vxs-word { animation: vxs-word .7s ease-out 1.25s backwards; }
      .vxs-play .vxs-meta { animation: vxs-word .6s ease-out 1.5s backwards; }
      .vxs-play .vxs-bottom { animation: vxs-progress 2.0s cubic-bezier(.6,0,.2,1) backwards; }
      @keyframes vxs-draw { from { stroke-dashoffset: 160; } to { stroke-dashoffset: 0; } }
      @keyframes vxs-bar { from { opacity: 0; transform: scaleY(.2); } to { opacity: 1; transform: scaleY(1); } }
      @keyframes vxs-glow { 0%,100% { opacity: 0; } 50% { opacity: .4; } }
      @keyframes vxs-word { from { opacity: 0; letter-spacing: 0.7em; } to { opacity: 1; letter-spacing: 0.42em; } }
      @keyframes vxs-progress { from { transform: scaleX(0); } to { transform: scaleX(1); } }
    `}</style>
  );
}

function Splash({ onSkip }) {
  const ref = React.useRef(null);
  const [stage, setStage] = React.useState(0);
  React.useEffect(() => {
    const t = setInterval(() => setStage((s) => Math.min(s + 1, SPLASH_STAGES.length - 1)), 420);
    return () => clearInterval(t);
  }, []);
  React.useEffect(() => {
    const el = ref.current;
    if (!el || (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches)) return;
    el.classList.add('vxs-play');
    const t = setTimeout(() => el && el.classList.remove('vxs-play'), 2500);
    return () => clearTimeout(t);
  }, []);
  const stroke = { fill: 'none', stroke: 'var(--pulse-400)', strokeWidth: 7, strokeLinecap: 'round', strokeLinejoin: 'round' };
  return (
    <div ref={ref} className="vxs-stage" onClick={onSkip} role="img" aria-label="Voxa Studio launching">
      <SplashStyles />
      <svg className="vxs-mark" viewBox="0 0 100 100">
        <path className="glow" d="M14,18 L50,86 L86,18" style={{ ...stroke, strokeWidth: 13, opacity: 0, filter: 'blur(9px)' }} />
        <path className="v" d="M14,18 L50,86 L86,18" style={{ ...stroke, strokeDasharray: 160, strokeDashoffset: 0 }} />
        <rect className="bar b1" x="38" y="34" width="7" height="22" rx="3.5" style={{ fill: 'var(--pulse-400)', opacity: 1, transformOrigin: 'center bottom' }} />
        <rect className="bar b2" x="50" y="26" width="7" height="30" rx="3.5" style={{ fill: 'var(--pulse-400)', opacity: 1, transformOrigin: 'center bottom' }} />
        <rect className="bar b3" x="62" y="38" width="7" height="18" rx="3.5" style={{ fill: 'var(--pulse-400)', opacity: 1, transformOrigin: 'center bottom' }} />
      </svg>
      <div className="vxs-word" style={{ fontFamily: 'var(--font-ui)', fontWeight: 700, fontSize: 23, letterSpacing: '0.42em', color: 'var(--text-1)', opacity: 1, paddingLeft: '0.42em' }}>
        VOXA <b style={{ fontWeight: 400, color: 'var(--text-3)' }}>STUDIO</b>
      </div>
      <div className="vxs-meta" style={{ fontFamily: 'var(--font-mono)', fontSize: 11, letterSpacing: '0.06em', color: 'var(--text-muted)', opacity: 1, display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{ width: 5, height: 5, borderRadius: '50%', background: 'var(--pulse-400)', boxShadow: '0 0 8px var(--pulse-400)' }}></span>
        {SPLASH_STAGES[stage]}{stage < SPLASH_STAGES.length - 1 ? '…' : ' · click to enter'}
      </div>
      <div className="vxs-bottom" style={{ position: 'absolute', left: 0, right: 0, bottom: 0, height: 3, background: 'var(--pulse-400)', transformOrigin: 'left', transform: 'scaleX(1)', opacity: 0.85 }}></div>
    </div>
  );
}

window.Splash = Splash;
