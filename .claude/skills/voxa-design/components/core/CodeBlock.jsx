import React from 'react';

const KEYWORDS = /\b(using|var|new|await|async|public|class|void|return|string|int|bool|this|true|false|null|namespace|record|static)\b/g;

function highlight(src) {
  // order matters: comments + strings first, then keywords/types/numbers
  const out = [];
  const re = /(\/\/[^\n]*)|("(?:[^"\\]|\\.)*")|(\b\d[\d_.]*\b)/g;
  let last = 0, m;
  const pushPlain = (text, keyBase) => {
    let l = 0, k = 0, mm;
    KEYWORDS.lastIndex = 0;
    const parts = [];
    while ((mm = KEYWORDS.exec(text))) {
      if (mm.index > l) parts.push(plainTypes(text.slice(l, mm.index), `${keyBase}p${k++}`));
      parts.push(<span key={`${keyBase}k${k++}`} className="tk-kw">{mm[0]}</span>);
      l = mm.index + mm[0].length;
    }
    if (l < text.length) parts.push(plainTypes(text.slice(l), `${keyBase}p${k++}`));
    out.push(...parts);
  };
  const plainTypes = (text, key) => {
    // PascalCase identifiers → type color
    const bits = text.split(/(\b[A-Z][A-Za-z0-9]+\b)/g);
    return (
      <React.Fragment key={key}>
        {bits.map((b, i) => (/^[A-Z][A-Za-z0-9]+$/.test(b) ? <span key={i} className="tk-ty">{b}</span> : b))}
      </React.Fragment>
    );
  };
  let i = 0;
  while ((m = re.exec(src))) {
    if (m.index > last) pushPlain(src.slice(last, m.index), `s${i}`);
    if (m[1]) out.push(<span key={`c${i}`} className="tk-cm">{m[1]}</span>);
    else if (m[2]) out.push(<span key={`q${i}`} className="tk-str">{m[2]}</span>);
    else out.push(<span key={`n${i}`} className="tk-num">{m[3]}</span>);
    last = m.index + m[0].length;
    i++;
  }
  if (last < src.length) pushPlain(src.slice(last), `e${i}`);
  return out;
}

/** VOXA CodeBlock — dark code panel with filename bar and light C#-ish highlighting. */
export function CodeBlock({ code, file, badge, actions, style, className = '' }) {
  return (
    <div className={`vx-code ${className}`} style={style}>
      {(file || badge || actions) && (
        <div className="vx-code__bar">
          <span className="vx-code__file">{file}</span>
          <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            {badge && <span className="vx-badge vx-badge--neutral">{badge}</span>}
            {actions}
          </span>
        </div>
      )}
      <pre><code>{highlight(code)}</code></pre>
    </div>
  );
}
