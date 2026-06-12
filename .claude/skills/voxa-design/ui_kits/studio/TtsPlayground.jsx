// Voxa Studio — TTS Playground (VST-002 §7): the v1 Voice Lab matured into a lab —
// take history, waveform scrubber, A/B/X blind test, stress phrases, batch bench.
const { Button: TtButton, Badge: TtBadge, Card: TtCard, Input: TtInput } = window.VOXADesignSystem_4f47fa;

const TT_VOICES = [
  { v: 'amy-low', eng: 'Piper', ttfb: 96, rtf: 0.21 },
  { v: 'amy-medium', eng: 'Piper', ttfb: 128, rtf: 0.29 },
  { v: 'ryan-high', eng: 'Piper', ttfb: 151, rtf: 0.34 },
  { v: 'af_sky', eng: 'Kokoro', ttfb: 204, rtf: 0.41 },
  { v: 'am_adam', eng: 'Kokoro', ttfb: 212, rtf: 0.44 },
];
const TT_STRESS = ['$1,204.50 on 03/14', 'read HTTP/2 aloud', 'the wound was wound', 'naïve café résumé', 'SELECT * FROM users;'];
const TT_TAKES = [{ t: 'amy-low', d: '2.4s', ttfb: 96 }, { t: 'amy-medium', d: '2.5s', ttfb: 128 }, { t: 'af_sky', d: '2.6s', ttfb: 204 }];
const TT_WAVE = Array.from({ length: 56 }, (_, i) => 0.25 + 0.7 * Math.abs(Math.sin(i * 0.5) * Math.cos(i * 0.17)));

function Scrubber() {
  const [pos, setPos] = React.useState(0.42);
  return (
    <div onClick={(e) => { const r = e.currentTarget.getBoundingClientRect(); setPos((e.clientX - r.left) / r.width); }}
      style={{ position: 'relative', display: 'flex', alignItems: 'flex-end', gap: 2, height: 56, cursor: 'pointer', padding: '0 2px' }}>
      {TT_WAVE.map((h, i) => (
        <div key={i} style={{ flex: 1, height: `${h * 100}%`, borderRadius: 999, background: i / TT_WAVE.length < pos ? 'var(--pulse-400)' : 'var(--ink-600)' }}></div>
      ))}
      <div style={{ position: 'absolute', top: -3, bottom: -3, left: `${pos * 100}%`, width: 2, background: 'var(--text-1)', boxShadow: '0 0 8px var(--pulse-400)' }}></div>
    </div>
  );
}

function TtsPlayground() {
  const [voice, setVoice] = React.useState('amy-low');
  const [text, setText] = React.useState('Your order shipped this morning and arrives Thursday.');
  const [revealed, setRevealed] = React.useState(false);
  return (
    <React.Fragment>
      <ViewBar title="TTS Playground" sub="real engines · TTFB / RTF · A/B/X blind · batch bench">
        <TtButton variant="secondary" size="sm" icon={<i data-lucide="download" style={{ width: 14, height: 14 }}></i>}>Export WAV</TtButton>
        <TtButton variant="primary" size="sm" icon={<i data-lucide="audio-lines" style={{ width: 14, height: 14 }}></i>}>Synthesize</TtButton>
      </ViewBar>
      <div style={{ flex: 1, overflowY: 'auto', padding: 20, display: 'flex', flexDirection: 'column', gap: 16 }}>
        <TtInput value={text} onChange={setText} style={{ width: '100%' }} />
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          <span className="vx-label" style={{ marginRight: 4 }}>stress phrases</span>
          {TT_STRESS.map((p) => (
            <button key={p} onClick={() => setText(p)} style={{
              padding: '5px 10px', borderRadius: 999, border: '1px solid var(--line-2)', background: 'var(--surface-2)',
              color: 'var(--text-2)', fontFamily: 'var(--font-mono)', fontSize: 11, cursor: 'pointer',
            }}>{p}</button>
          ))}
        </div>

        <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start', flexWrap: 'wrap' }}>
          {/* voice catalog */}
          <TtCard style={{ width: 280, flex: 'none', padding: 16 }}>
            <div className="vx-label">Voice catalog</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 12 }}>
              {TT_VOICES.map((v) => {
                const on = voice === v.v;
                return (
                  <button key={v.v} onClick={() => setVoice(v.v)} style={{
                    display: 'grid', gridTemplateColumns: '1fr auto auto', alignItems: 'center', gap: 10, padding: '9px 11px',
                    borderRadius: 'var(--r-md)', cursor: 'pointer', textAlign: 'left',
                    border: '1px solid', borderColor: on ? 'var(--line-3)' : 'var(--line-1)', background: on ? 'var(--surface-3)' : 'var(--surface-2)',
                  }}>
                    <span style={{ minWidth: 0 }}>
                      <span style={{ fontSize: 13, color: 'var(--text-1)', fontWeight: on ? 600 : 400 }}>{v.v}</span>
                      <span style={{ display: 'block', fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-muted)' }}>{v.eng}</span>
                    </span>
                    <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--pulse-300)' }}>{v.ttfb}ms</span>
                    <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>{v.rtf}</span>
                  </button>
                );
              })}
            </div>
          </TtCard>

          <div style={{ flex: 1, minWidth: 320, display: 'flex', flexDirection: 'column', gap: 16 }}>
            {/* synthesis + take history */}
            <TtCard style={{ padding: 18 }}>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Synthesis · {voice}</div>
                <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>scrub to play</span>
              </div>
              <div style={{ marginTop: 12 }}><Scrubber /></div>
              <div style={{ borderTop: '1px solid var(--line-1)', marginTop: 14, paddingTop: 12, display: 'flex', flexDirection: 'column', gap: 8 }}>
                <span className="vx-label">take history</span>
                {TT_TAKES.map((tk, i) => (
                  <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                    <button style={{ width: 26, height: 26, borderRadius: '50%', flex: 'none', border: '1px solid var(--line-2)', background: 'var(--surface-3)', color: 'var(--pulse-400)', cursor: 'pointer', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>
                      <i data-lucide="play" style={{ width: 12, height: 12 }}></i>
                    </button>
                    <span style={{ flex: 1, fontSize: 13, color: 'var(--text-2)' }}>{tk.t}</span>
                    <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-3)' }}>{tk.d} · {tk.ttfb}ms</span>
                  </div>
                ))}
              </div>
            </TtCard>

            <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
              {/* A/B/X */}
              <TtCard style={{ flex: 1, minWidth: 240, padding: 18 }}>
                <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>A / B / X blind test</div>
                <div style={{ display: 'flex', gap: 8, marginTop: 14 }}>
                  {['A', 'B', 'X'].map((k) => (
                    <button key={k} style={{ flex: 1, padding: '10px 0', borderRadius: 'var(--r-md)', border: '1px solid var(--line-2)', background: k === 'X' ? 'var(--accent-soft)' : 'var(--surface-3)', color: k === 'X' ? 'var(--pulse-300)' : 'var(--text-1)', fontWeight: 600, cursor: 'pointer' }}>{k}</button>
                  ))}
                </div>
                <div style={{ marginTop: 12, fontSize: 12.5, color: 'var(--text-3)' }}>
                  {revealed
                    ? <span>X was <b style={{ color: 'var(--text-1)' }}>B · af_sky</b>. You picked B.</span>
                    : 'X is randomly A or B. Vote, then reveal.'}
                </div>
                <TtButton variant="secondary" size="sm" onClick={() => setRevealed((x) => !x)} style={{ marginTop: 12 }}>{revealed ? 'Reset' : 'Reveal'}</TtButton>
              </TtCard>

              {/* batch bench */}
              <TtCard style={{ flex: 1, minWidth: 240, padding: 18 }}>
                <div className="vx-label" style={{ color: 'var(--pulse-300)' }}>Batch bench · phrase deck</div>
                <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: 12, fontSize: 12 }}>
                  <thead>
                    <tr style={{ color: 'var(--text-muted)', fontFamily: 'var(--font-mono)', fontSize: 10, textTransform: 'uppercase', letterSpacing: '0.06em' }}>
                      <td style={{ padding: '0 0 8px' }}>voice</td><td style={{ textAlign: 'right' }}>p50</td><td style={{ textAlign: 'right' }}>p95</td><td style={{ textAlign: 'right' }}>rtf</td>
                    </tr>
                  </thead>
                  <tbody style={{ fontFamily: 'var(--font-mono)' }}>
                    {TT_VOICES.slice(0, 4).map((v) => (
                      <tr key={v.v} style={{ borderTop: '1px solid var(--line-1)', color: 'var(--text-2)' }}>
                        <td style={{ padding: '7px 0', fontFamily: 'var(--font-ui)' }}>{v.v}</td>
                        <td style={{ textAlign: 'right', color: 'var(--pulse-300)' }}>{v.ttfb}</td>
                        <td style={{ textAlign: 'right' }}>{Math.round(v.ttfb * 1.4)}</td>
                        <td style={{ textAlign: 'right' }}>{v.rtf}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </TtCard>
            </div>
          </div>
        </div>
      </div>
    </React.Fragment>
  );
}

window.TtsPlayground = TtsPlayground;
