/* @ds-bundle: {"format":3,"namespace":"VOXADesignSystem_4f47fa","components":[{"name":"Badge","sourcePath":"components/core/Badge.jsx"},{"name":"Button","sourcePath":"components/core/Button.jsx"},{"name":"Card","sourcePath":"components/core/Card.jsx"},{"name":"CodeBlock","sourcePath":"components/core/CodeBlock.jsx"},{"name":"IconButton","sourcePath":"components/core/IconButton.jsx"},{"name":"Tabs","sourcePath":"components/core/Tabs.jsx"},{"name":"Input","sourcePath":"components/forms/Input.jsx"},{"name":"Select","sourcePath":"components/forms/Select.jsx"},{"name":"Switch","sourcePath":"components/forms/Switch.jsx"},{"name":"MetricStat","sourcePath":"components/voice/MetricStat.jsx"},{"name":"PipelineFlow","sourcePath":"components/voice/PipelineFlow.jsx"},{"name":"PipelineNode","sourcePath":"components/voice/PipelineNode.jsx"},{"name":"StatusPill","sourcePath":"components/voice/StatusPill.jsx"},{"name":"TranscriptLine","sourcePath":"components/voice/TranscriptLine.jsx"},{"name":"VoiceOrb","sourcePath":"components/voice/VoiceOrb.jsx"},{"name":"Waveform","sourcePath":"components/voice/Waveform.jsx"}],"sourceHashes":{"components/core/Badge.jsx":"5103d4d7cddb","components/core/Button.jsx":"132e4bdc5e02","components/core/Card.jsx":"35dfd581e283","components/core/CodeBlock.jsx":"7c74bd0ad031","components/core/IconButton.jsx":"ac3ac9b66795","components/core/Tabs.jsx":"b7b3b8f7fb61","components/forms/Input.jsx":"25b72bd0f7a8","components/forms/Select.jsx":"4f815844b3ef","components/forms/Switch.jsx":"48e1575283dd","components/voice/MetricStat.jsx":"80ecf9920cd6","components/voice/PipelineFlow.jsx":"428cc1f58dfb","components/voice/PipelineNode.jsx":"e7f40d710e4c","components/voice/StatusPill.jsx":"d88e75f64859","components/voice/TranscriptLine.jsx":"c1643dfb6077","components/voice/VoiceOrb.jsx":"549b8350d621","components/voice/Waveform.jsx":"9d4bd4ff5354","ui_kits/console/ConsoleShell.jsx":"383302f7e810","ui_kits/console/LiveSessionView.jsx":"d7988dcbda5c","ui_kits/console/SessionsView.jsx":"ce9fb809191c","ui_kits/console/app.jsx":"78996f9d8508","ui_kits/studio/MetricsWorkbench.jsx":"e9cddbcc128b","ui_kits/studio/PipelineBuilder.jsx":"3cd1ede11840","ui_kits/studio/Splash.jsx":"98d283c3741e","ui_kits/studio/SttPlayground.jsx":"338e23b2d98f","ui_kits/studio/StudioShell.jsx":"23db5f2538c9","ui_kits/studio/SupportingViews.jsx":"7f5ec188b583","ui_kits/studio/TalkView.jsx":"f78415467e4d","ui_kits/studio/TtsPlayground.jsx":"9933a3a7b14c","ui_kits/studio/app.jsx":"74b330556313","ui_kits/studio/canvasNodes.jsx":"39e6d90dbc41","ui_kits/studio/icons.js":"9a6aa9b8304e","ui_kits/website/Features.jsx":"bc702013887b","ui_kits/website/Footer.jsx":"7dda6541f081","ui_kits/website/Hero.jsx":"2a8808473443","ui_kits/website/SiteNav.jsx":"89111ee36a46","ui_kits/website/site.jsx":"354f1d2bdfc3"},"inlinedExternals":[],"unexposedExports":[]} */

(() => {

const __ds_ns = (window.VOXADesignSystem_4f47fa = window.VOXADesignSystem_4f47fa || {});

const __ds_scope = {};

(__ds_ns.__errors = __ds_ns.__errors || []);

// components/core/Badge.jsx
try { (() => {
/** VOXA Badge — uppercase mono pill for statuses and taxonomy. */
function Badge({
  tone = 'neutral',
  children,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("span", {
    className: `vx-badge vx-badge--${tone} ${className}`,
    style: style
  }, children);
}
Object.assign(__ds_scope, { Badge });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Badge.jsx", error: String((e && e.message) || e) }); }

// components/core/Button.jsx
try { (() => {
/** VOXA Button — primary / secondary / ghost / danger. */
function Button({
  variant = 'primary',
  size = 'md',
  icon,
  children,
  disabled,
  onClick,
  type = 'button',
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("button", {
    type: type,
    className: `vx-btn vx-btn--${variant} vx-btn--${size} ${className}`,
    disabled: disabled,
    onClick: onClick,
    style: style
  }, icon, children);
}
Object.assign(__ds_scope, { Button });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Button.jsx", error: String((e && e.message) || e) }); }

// components/core/Card.jsx
try { (() => {
/** VOXA Card — raised dark surface. */
function Card({
  padded = true,
  interactive = false,
  glow = false,
  children,
  onClick,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-card ${padded ? 'vx-card--pad' : ''} ${interactive ? 'vx-card--interactive' : ''} ${glow ? 'vx-card--glow' : ''} ${className}`,
    onClick: onClick,
    style: style
  }, children);
}
Object.assign(__ds_scope, { Card });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Card.jsx", error: String((e && e.message) || e) }); }

// components/core/CodeBlock.jsx
try { (() => {
const KEYWORDS = /\b(using|var|new|await|async|public|class|void|return|string|int|bool|this|true|false|null|namespace|record|static)\b/g;
function highlight(src) {
  // order matters: comments + strings first, then keywords/types/numbers
  const out = [];
  const re = /(\/\/[^\n]*)|("(?:[^"\\]|\\.)*")|(\b\d[\d_.]*\b)/g;
  let last = 0,
    m;
  const pushPlain = (text, keyBase) => {
    let l = 0,
      k = 0,
      mm;
    KEYWORDS.lastIndex = 0;
    const parts = [];
    while (mm = KEYWORDS.exec(text)) {
      if (mm.index > l) parts.push(plainTypes(text.slice(l, mm.index), `${keyBase}p${k++}`));
      parts.push(/*#__PURE__*/React.createElement("span", {
        key: `${keyBase}k${k++}`,
        className: "tk-kw"
      }, mm[0]));
      l = mm.index + mm[0].length;
    }
    if (l < text.length) parts.push(plainTypes(text.slice(l), `${keyBase}p${k++}`));
    out.push(...parts);
  };
  const plainTypes = (text, key) => {
    // PascalCase identifiers → type color
    const bits = text.split(/(\b[A-Z][A-Za-z0-9]+\b)/g);
    return /*#__PURE__*/React.createElement(React.Fragment, {
      key: key
    }, bits.map((b, i) => /^[A-Z][A-Za-z0-9]+$/.test(b) ? /*#__PURE__*/React.createElement("span", {
      key: i,
      className: "tk-ty"
    }, b) : b));
  };
  let i = 0;
  while (m = re.exec(src)) {
    if (m.index > last) pushPlain(src.slice(last, m.index), `s${i}`);
    if (m[1]) out.push(/*#__PURE__*/React.createElement("span", {
      key: `c${i}`,
      className: "tk-cm"
    }, m[1]));else if (m[2]) out.push(/*#__PURE__*/React.createElement("span", {
      key: `q${i}`,
      className: "tk-str"
    }, m[2]));else out.push(/*#__PURE__*/React.createElement("span", {
      key: `n${i}`,
      className: "tk-num"
    }, m[3]));
    last = m.index + m[0].length;
    i++;
  }
  if (last < src.length) pushPlain(src.slice(last), `e${i}`);
  return out;
}

/** VOXA CodeBlock — dark code panel with filename bar and light C#-ish highlighting. */
function CodeBlock({
  code,
  file,
  badge,
  actions,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-code ${className}`,
    style: style
  }, (file || badge || actions) && /*#__PURE__*/React.createElement("div", {
    className: "vx-code__bar"
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-code__file"
  }, file), /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 8
    }
  }, badge && /*#__PURE__*/React.createElement("span", {
    className: "vx-badge vx-badge--neutral"
  }, badge), actions)), /*#__PURE__*/React.createElement("pre", null, /*#__PURE__*/React.createElement("code", null, highlight(code))));
}
Object.assign(__ds_scope, { CodeBlock });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/CodeBlock.jsx", error: String((e && e.message) || e) }); }

// components/core/IconButton.jsx
try { (() => {
/** VOXA IconButton — square hit target for icon-only actions. */
function IconButton({
  size = 'md',
  outline = false,
  label,
  children,
  disabled,
  onClick,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("button", {
    type: "button",
    "aria-label": label,
    title: label,
    className: `vx-iconbtn ${size === 'sm' ? 'vx-iconbtn--sm' : ''} ${outline ? 'vx-iconbtn--outline' : ''} ${className}`,
    disabled: disabled,
    onClick: onClick,
    style: style
  }, children);
}
Object.assign(__ds_scope, { IconButton });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/IconButton.jsx", error: String((e && e.message) || e) }); }

// components/core/Tabs.jsx
try { (() => {
const {
  useState
} = React;
/** VOXA Tabs — underline style view switcher. */
function Tabs({
  tabs,
  active,
  defaultTab,
  onChange,
  style,
  className = ''
}) {
  const [internal, setInternal] = useState(defaultTab ?? tabs[0]);
  const current = active ?? internal;
  const pick = t => {
    setInternal(t);
    onChange && onChange(t);
  };
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-tabs ${className}`,
    role: "tablist",
    style: style
  }, tabs.map(t => /*#__PURE__*/React.createElement("button", {
    key: t,
    role: "tab",
    "aria-selected": current === t,
    className: "vx-tab",
    onClick: () => pick(t)
  }, t)));
}
Object.assign(__ds_scope, { Tabs });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Tabs.jsx", error: String((e && e.message) || e) }); }

// components/forms/Input.jsx
try { (() => {
/** VOXA Input — labeled text field. */
function Input({
  label,
  hint,
  error,
  mono = false,
  type = 'text',
  value,
  defaultValue,
  placeholder,
  onChange,
  disabled,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-field ${error ? 'vx-field--error' : ''} ${className}`,
    style: style
  }, label && /*#__PURE__*/React.createElement("label", null, label), /*#__PURE__*/React.createElement("input", {
    className: `vx-input ${mono ? 'vx-input--mono' : ''}`,
    type: type,
    value: value,
    defaultValue: defaultValue,
    placeholder: placeholder,
    onChange: onChange ? e => onChange(e.target.value) : undefined,
    disabled: disabled
  }), (error || hint) && /*#__PURE__*/React.createElement("span", {
    className: "vx-field__hint"
  }, error || hint));
}
Object.assign(__ds_scope, { Input });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Input.jsx", error: String((e && e.message) || e) }); }

// components/forms/Select.jsx
try { (() => {
/** VOXA Select — labeled native select, styled. */
function Select({
  label,
  hint,
  options,
  value,
  defaultValue,
  onChange,
  disabled,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-field ${className}`,
    style: style
  }, label && /*#__PURE__*/React.createElement("label", null, label), /*#__PURE__*/React.createElement("select", {
    className: "vx-select",
    value: value,
    defaultValue: defaultValue,
    onChange: onChange ? e => onChange(e.target.value) : undefined,
    disabled: disabled
  }, options.map(o => {
    const opt = typeof o === 'string' ? {
      value: o,
      label: o
    } : o;
    return /*#__PURE__*/React.createElement("option", {
      key: opt.value,
      value: opt.value
    }, opt.label);
  })), hint && /*#__PURE__*/React.createElement("span", {
    className: "vx-field__hint"
  }, hint));
}
Object.assign(__ds_scope, { Select });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Select.jsx", error: String((e && e.message) || e) }); }

// components/forms/Switch.jsx
try { (() => {
const {
  useState
} = React;
/** VOXA Switch — toggle with optional inline label. */
function Switch({
  checked,
  defaultChecked = false,
  onChange,
  label,
  disabled,
  style,
  className = ''
}) {
  const [internal, setInternal] = useState(defaultChecked);
  const isOn = checked ?? internal;
  const toggle = () => {
    setInternal(!isOn);
    onChange && onChange(!isOn);
  };
  const btn = /*#__PURE__*/React.createElement("button", {
    type: "button",
    role: "switch",
    "aria-checked": isOn,
    className: `vx-switch ${className}`,
    onClick: toggle,
    disabled: disabled,
    style: label ? undefined : style
  });
  if (!label) return btn;
  return /*#__PURE__*/React.createElement("span", {
    className: "vx-switch-row",
    style: style
  }, btn, /*#__PURE__*/React.createElement("span", {
    onClick: disabled ? undefined : toggle,
    style: {
      cursor: disabled ? 'default' : 'pointer'
    }
  }, label));
}
Object.assign(__ds_scope, { Switch });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Switch.jsx", error: String((e && e.message) || e) }); }

// components/voice/MetricStat.jsx
try { (() => {
/** VOXA MetricStat — big mono number with label and delta. */
function MetricStat({
  label,
  value,
  unit,
  delta,
  deltaTone = 'flat',
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-metric ${className}`,
    style: style
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label"
  }, label), /*#__PURE__*/React.createElement("span", {
    className: "vx-metric__value"
  }, value, unit && /*#__PURE__*/React.createElement("small", null, unit)), delta && /*#__PURE__*/React.createElement("span", {
    className: `vx-metric__delta vx-metric__delta--${deltaTone}`
  }, delta));
}
Object.assign(__ds_scope, { MetricStat });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/MetricStat.jsx", error: String((e && e.message) || e) }); }

// components/voice/PipelineNode.jsx
try { (() => {
const STAGE_COLORS = {
  vad: 'var(--stage-vad)',
  audio: 'var(--stage-vad)',
  // legacy alias — audio frames are grey
  stt: 'var(--stage-stt)',
  agent: 'var(--stage-agent)',
  llm: 'var(--stage-agent)',
  // legacy alias
  tts: 'var(--stage-tts)',
  out: 'var(--stage-out)'
};

/** VOXA PipelineNode — one processor in a frame pipeline. */
function PipelineNode({
  stage = 'stt',
  kind,
  name,
  meta,
  active = false,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-node ${active ? 'vx-node--active' : ''} ${className}`,
    style: {
      '--node-c': STAGE_COLORS[stage] || STAGE_COLORS.stt,
      ...style
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-node__kind"
  }, kind ?? stage), /*#__PURE__*/React.createElement("span", {
    className: "vx-node__name"
  }, name), meta && /*#__PURE__*/React.createElement("span", {
    className: "vx-node__meta"
  }, meta));
}
Object.assign(__ds_scope, { PipelineNode });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/PipelineNode.jsx", error: String((e && e.message) || e) }); }

// components/voice/PipelineFlow.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
/** VOXA PipelineFlow — a chain of PipelineNodes with frame-travel links. */
function PipelineFlow({
  nodes,
  running = false,
  activeIndex = -1,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-flow ${className}`,
    style: style
  }, nodes.map((n, i) => /*#__PURE__*/React.createElement(React.Fragment, {
    key: i
  }, i > 0 && /*#__PURE__*/React.createElement("span", {
    className: `vx-flow__link ${running ? 'vx-flow__link--active' : ''}`
  }), /*#__PURE__*/React.createElement(__ds_scope.PipelineNode, _extends({}, n, {
    active: i === activeIndex
  })))));
}
Object.assign(__ds_scope, { PipelineFlow });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/PipelineFlow.jsx", error: String((e && e.message) || e) }); }

// components/voice/StatusPill.jsx
try { (() => {
const LABELS = {
  live: 'Live',
  connecting: 'Connecting',
  idle: 'Idle',
  ended: 'Ended',
  error: 'Error'
};

/** VOXA StatusPill — session/pipeline state with signal dot. */
function StatusPill({
  status = 'idle',
  label,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("span", {
    className: `vx-status vx-status--${status} ${className}`,
    style: style
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-status__dot"
  }), label ?? LABELS[status] ?? status);
}
Object.assign(__ds_scope, { StatusPill });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/StatusPill.jsx", error: String((e && e.message) || e) }); }

// components/voice/TranscriptLine.jsx
try { (() => {
/** VOXA TranscriptLine — one utterance in a session transcript. */
function TranscriptLine({
  role = 'user',
  text,
  time,
  partial = false,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("div", {
    className: `vx-transcript-line vx-transcript-line--${role} ${partial ? 'vx-transcript-line--partial' : ''} ${className}`,
    style: style
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-transcript-line__role"
  }, role), /*#__PURE__*/React.createElement("span", {
    className: "vx-transcript-line__text"
  }, text), time && /*#__PURE__*/React.createElement("span", {
    className: "vx-transcript-line__time"
  }, time));
}
Object.assign(__ds_scope, { TranscriptLine });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/TranscriptLine.jsx", error: String((e && e.message) || e) }); }

// components/voice/VoiceOrb.jsx
try { (() => {
/** VOXA VoiceOrb — the agent presence orb. */
function VoiceOrb({
  size = 96,
  live = false,
  style,
  className = ''
}) {
  return /*#__PURE__*/React.createElement("span", {
    className: `vx-orb ${live ? 'vx-orb--live' : ''} ${className}`,
    style: {
      display: 'inline-block',
      width: size,
      height: size,
      ...style
    },
    "aria-hidden": "true"
  });
}
Object.assign(__ds_scope, { VoiceOrb });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/VoiceOrb.jsx", error: String((e && e.message) || e) }); }

// components/voice/Waveform.jsx
try { (() => {
const STATIC_LEVELS = [0.5, 0.85, 0.4, 1, 0.65, 0.9, 0.35, 0.7, 1, 0.55, 0.8, 0.45];

/** VOXA Waveform — voice activity bars (the brand's core motif). */
function Waveform({
  bars = 12,
  height = 28,
  live = false,
  gradient = false,
  muted = false,
  levels,
  style,
  className = ''
}) {
  const lv = levels && levels.length ? levels : STATIC_LEVELS;
  return /*#__PURE__*/React.createElement("span", {
    className: `vx-wave ${live ? 'vx-wave--live' : ''} ${gradient ? 'vx-wave--grad' : ''} ${muted ? 'vx-wave--muted' : ''} ${className}`,
    style: {
      height,
      ...style
    },
    "aria-hidden": "true"
  }, Array.from({
    length: bars
  }).map((_, i) => /*#__PURE__*/React.createElement("span", {
    key: i,
    className: "vx-wave__bar",
    style: {
      height: `${Math.round(lv[i % lv.length] * 100)}%`,
      animationDelay: live ? `${i * 87 % 900}ms` : undefined
    }
  })));
}
Object.assign(__ds_scope, { Waveform });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/voice/Waveform.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/ConsoleShell.jsx
try { (() => {
const {
  IconButton,
  Badge,
  StatusPill
} = window.VOXADesignSystem_4f47fa;
const NAV = [{
  id: 'sessions',
  icon: 'activity',
  label: 'Sessions'
}, {
  id: 'pipelines',
  icon: 'workflow',
  label: 'Pipelines'
}, {
  id: 'voices',
  icon: 'audio-lines',
  label: 'Voices'
}, {
  id: 'keys',
  icon: 'key-round',
  label: 'Keys'
}, {
  id: 'settings',
  icon: 'settings-2',
  label: 'Settings'
}];
function ConsoleShell({
  nav,
  onNav,
  title,
  actions,
  children
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      height: '100vh',
      overflow: 'hidden'
    }
  }, /*#__PURE__*/React.createElement("aside", {
    style: {
      width: 'var(--sidebar-w)',
      flex: 'none',
      background: 'var(--bg-panel)',
      borderRight: '1px solid var(--line-1)',
      display: 'flex',
      flexDirection: 'column',
      padding: '18px 12px'
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: (window.DS_BASE || '../..') + '/assets/voxa-logo.svg',
    alt: "VOXA",
    style: {
      height: 30,
      alignSelf: 'flex-start',
      margin: '2px 6px 22px'
    }
  }), /*#__PURE__*/React.createElement("nav", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 2
    }
  }, NAV.map(n => /*#__PURE__*/React.createElement("button", {
    key: n.id,
    onClick: () => onNav(n.id),
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 10,
      padding: '9px 10px',
      background: nav === n.id ? 'var(--surface-3)' : 'transparent',
      border: '1px solid',
      borderColor: nav === n.id ? 'var(--line-1)' : 'transparent',
      borderRadius: 'var(--r-md)',
      cursor: 'pointer',
      color: nav === n.id ? 'var(--text-1)' : 'var(--text-3)',
      font: '500 13px var(--font-body)',
      textAlign: 'left',
      transition: 'background var(--dur-1) var(--ease-out), color var(--dur-1) var(--ease-out)'
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": n.icon,
    style: {
      width: 16,
      height: 16
    }
  }), n.label))), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 'auto',
      display: 'flex',
      flexDirection: 'column',
      gap: 12,
      padding: '0 6px'
    }
  }, /*#__PURE__*/React.createElement(Badge, {
    tone: "neutral"
  }, "westus2 \xB7 prod"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 10
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 28,
      height: 28,
      borderRadius: '50%',
      flex: 'none',
      background: 'var(--surface-4)',
      border: '1px solid var(--line-2)',
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center',
      font: '600 11px var(--font-mono)',
      color: 'var(--text-2)'
    }
  }, "MK"), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "m.kovac@contoso.dev")))), /*#__PURE__*/React.createElement("main", {
    style: {
      flex: 1,
      display: 'flex',
      flexDirection: 'column',
      minWidth: 0
    }
  }, /*#__PURE__*/React.createElement("header", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 16,
      padding: '14px 24px',
      borderBottom: '1px solid var(--line-1)',
      background: 'var(--glass-bg)',
      backdropFilter: 'var(--blur-glass)'
    }
  }, /*#__PURE__*/React.createElement("h1", {
    style: {
      fontSize: 20,
      flex: 1,
      minWidth: 0,
      whiteSpace: 'nowrap',
      overflow: 'hidden',
      textOverflow: 'ellipsis'
    }
  }, title), actions), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 24
    }
  }, children)));
}
window.ConsoleShell = ConsoleShell;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/ConsoleShell.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/LiveSessionView.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
const {
  Card,
  Button,
  Tabs,
  StatusPill,
  Waveform,
  MetricStat,
  PipelineFlow,
  TranscriptLine,
  Switch,
  Select
} = window.VOXADesignSystem_4f47fa;
const TRANSCRIPT = [{
  role: 'user',
  text: "Hi — what's the status of my order?",
  time: '00:08.120'
}, {
  role: 'agent',
  text: 'Let me check that for you. Can you confirm the order number?',
  time: '00:09.480'
}, {
  role: 'user',
  text: 'It should be 4-4-2-1.',
  time: '00:14.900'
}, {
  role: 'system',
  text: 'tool: Orders.Lookup("4421") → shipped',
  time: '00:15.310'
}, {
  role: 'agent',
  text: 'Order 4421 shipped this morning via FedEx —',
  time: '00:16.040'
}, {
  role: 'user',
  text: 'Oh wait, sorry —',
  time: '00:17.610'
}, {
  role: 'system',
  text: 'frame: interruption · agent yielded in 96 ms',
  time: '00:17.702'
}];
const FRAMES = [{
  role: 'system',
  text: 'AudioRawFrame · 320 bytes · 16 kHz',
  time: '00:17.690'
}, {
  role: 'system',
  text: 'UserStartedSpeakingFrame',
  time: '00:17.698'
}, {
  role: 'system',
  text: 'StartInterruptionFrame → downstream',
  time: '00:17.702'
}, {
  role: 'system',
  text: 'TTSStoppedFrame · AzureNeuralTts',
  time: '00:17.745'
}, {
  role: 'system',
  text: 'TranscriptionFrame · "Oh wait, sorry —"',
  time: '00:18.230'
}];
function LiveSessionView({
  session,
  tick
}) {
  const [tab, setTab] = React.useState('Transcript');
  const visible = TRANSCRIPT.slice(0, Math.min(2 + tick, TRANSCRIPT.length));
  const partial = tick % 6 === 5;
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: '1fr 300px',
      gap: 24,
      maxWidth: 1080,
      alignItems: 'start'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 20,
      minWidth: 0
    }
  }, /*#__PURE__*/React.createElement(Card, {
    padded: true,
    style: {
      overflowX: 'auto'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      display: 'block',
      marginBottom: 12
    }
  }, "Pipeline \xB7 support-voice"), /*#__PURE__*/React.createElement(PipelineFlow, {
    running: true,
    activeIndex: tick % 4,
    nodes: [{
      stage: 'audio',
      name: 'WebRtcTransport',
      meta: '48 kHz · opus'
    }, {
      stage: 'stt',
      name: 'AzureSpeechStt',
      meta: 'p50 118 ms'
    }, {
      stage: 'llm',
      name: 'AgentTurn',
      meta: 'gpt-4o · MAF'
    }, {
      stage: 'tts',
      name: 'AzureNeuralTts',
      meta: 'en-US-Ava'
    }]
  })), /*#__PURE__*/React.createElement(Card, {
    padded: false
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '4px 20px 0'
    }
  }, /*#__PURE__*/React.createElement(Tabs, {
    tabs: ['Transcript', 'Frames', 'Metrics'],
    active: tab,
    onChange: setTab
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '6px 20px 14px',
      minHeight: 260
    }
  }, tab === 'Transcript' && /*#__PURE__*/React.createElement("div", null, visible.map((l, i) => /*#__PURE__*/React.createElement(TranscriptLine, _extends({
    key: i
  }, l))), partial && /*#__PURE__*/React.createElement(TranscriptLine, {
    role: "agent",
    text: "No problem \u2014 take your",
    partial: true
  })), tab === 'Frames' && /*#__PURE__*/React.createElement("div", null, FRAMES.map((l, i) => /*#__PURE__*/React.createElement(TranscriptLine, _extends({
    key: i
  }, l)))), tab === 'Metrics' && /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(3, 1fr)',
      gap: 24,
      padding: '18px 0'
    }
  }, /*#__PURE__*/React.createElement(MetricStat, {
    label: "TTFB",
    value: "388",
    unit: "ms",
    delta: "\u25BC 24 ms vs p50",
    deltaTone: "good"
  }), /*#__PURE__*/React.createElement(MetricStat, {
    label: "STT latency",
    value: "118",
    unit: "ms",
    delta: "\u2014 stable",
    deltaTone: "flat"
  }), /*#__PURE__*/React.createElement(MetricStat, {
    label: "Turns",
    value: "14",
    delta: "\u25B2 streaming",
    deltaTone: "good"
  }))))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(Card, {
    glow: true
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      marginBottom: 14
    }
  }, /*#__PURE__*/React.createElement(StatusPill, {
    status: "live"
  }), /*#__PURE__*/React.createElement("code", {
    style: {
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, session.duration)), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 14
    }
  }, /*#__PURE__*/React.createElement(Waveform, {
    live: true,
    gradient: true,
    bars: 14,
    height: 30
  }), /*#__PURE__*/React.createElement("code", {
    style: {
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, "en-US-Ava"))), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      display: 'block',
      marginBottom: 14
    }
  }, "Session settings"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 14
    }
  }, /*#__PURE__*/React.createElement(Switch, {
    label: "Interruptions",
    defaultChecked: true
  }), /*#__PURE__*/React.createElement(Switch, {
    label: "Noise suppression",
    defaultChecked: true
  }), /*#__PURE__*/React.createElement(Switch, {
    label: "Record audio"
  }), /*#__PURE__*/React.createElement(Select, {
    label: "Voice",
    options: ['en-US-Ava', 'en-US-Andrew', 'en-GB-Sonia']
  }))), /*#__PURE__*/React.createElement(Button, {
    variant: "danger"
  }, "End session")));
}
window.LiveSessionView = LiveSessionView;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/LiveSessionView.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/SessionsView.jsx
try { (() => {
const {
  Card,
  Badge,
  StatusPill,
  Waveform,
  MetricStat,
  Button
} = window.VOXADesignSystem_4f47fa;
function SessionsView({
  sessions,
  onOpen
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 24,
      maxWidth: 980
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(4, 1fr)',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement(MetricStat, {
    label: "Active sessions",
    value: "23",
    delta: "\u25B2 4 last hour",
    deltaTone: "good"
  })), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement(MetricStat, {
    label: "TTFB p50",
    value: "412",
    unit: "ms",
    delta: "\u25BC 38 ms",
    deltaTone: "good"
  })), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement(MetricStat, {
    label: "Interruptions",
    value: "8.2",
    unit: "%",
    delta: "\u2014 flat",
    deltaTone: "flat"
  })), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement(MetricStat, {
    label: "Error rate",
    value: "0.4",
    unit: "%",
    delta: "\u25B2 0.1 pt",
    deltaTone: "bad"
  }))), /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'baseline',
      gap: 12,
      marginBottom: 12
    }
  }, /*#__PURE__*/React.createElement("h2", {
    style: {
      fontSize: 16,
      fontWeight: 600
    }
  }, "Sessions"), /*#__PURE__*/React.createElement("span", {
    className: "vx-label"
  }, "today \xB7 1,284 total")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 8
    }
  }, sessions.map(s => /*#__PURE__*/React.createElement(Card, {
    key: s.id,
    interactive: true,
    padded: false,
    onClick: () => onOpen(s),
    style: {
      display: 'grid',
      gridTemplateColumns: '110px 1fr 120px 110px 90px 90px',
      alignItems: 'center',
      gap: 16,
      padding: '12px 16px'
    }
  }, /*#__PURE__*/React.createElement("code", {
    style: {
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, s.id), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 13,
      fontWeight: 500
    }
  }, s.pipeline), /*#__PURE__*/React.createElement(StatusPill, {
    status: s.status
  }), s.status === 'live' ? /*#__PURE__*/React.createElement(Waveform, {
    live: true,
    bars: 9,
    height: 16
  }) : /*#__PURE__*/React.createElement(Waveform, {
    muted: true,
    bars: 9,
    height: 16
  }), /*#__PURE__*/React.createElement("code", {
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, s.duration), /*#__PURE__*/React.createElement(Badge, {
    tone: "neutral"
  }, s.region))))));
}
window.SessionsView = SessionsView;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/SessionsView.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/app.jsx
try { (() => {
const {
  Button,
  StatusPill
} = window.VOXADesignSystem_4f47fa;
const SESSIONS = [{
  id: 'sx_4f81a2',
  pipeline: 'support-voice',
  status: 'live',
  duration: '04:12',
  region: 'westus2'
}, {
  id: 'sx_4f7b9c',
  pipeline: 'support-voice',
  status: 'live',
  duration: '01:48',
  region: 'westus2'
}, {
  id: 'sx_4f7902',
  pipeline: 'outbound-reminders',
  status: 'connecting',
  duration: '00:03',
  region: 'eastus'
}, {
  id: 'sx_4f76e1',
  pipeline: 'support-voice',
  status: 'ended',
  duration: '06:31',
  region: 'westus2'
}, {
  id: 'sx_4f74d0',
  pipeline: 'kiosk-concierge',
  status: 'ended',
  duration: '02:54',
  region: 'swedencentral'
}, {
  id: 'sx_4f71b8',
  pipeline: 'support-voice',
  status: 'error',
  duration: '00:41',
  region: 'eastus'
}];
function App() {
  const [nav, setNav] = React.useState('sessions');
  const [open, setOpen] = React.useState(null);
  const [tick, setTick] = React.useState(0);
  React.useEffect(() => {
    if (!open) return;
    const t = setInterval(() => setTick(x => x + 1), 1800);
    return () => clearInterval(t);
  }, [open]);
  React.useEffect(() => {
    window.lucide && lucide.createIcons({
      attrs: {
        'stroke-width': 1.75
      }
    });
  });
  const title = open ? `Session ${open.id}` : nav === 'sessions' ? 'Sessions' : 'Sessions';
  return /*#__PURE__*/React.createElement(ConsoleShell, {
    nav: nav,
    onNav: id => {
      setNav('sessions');
      setOpen(null);
    },
    title: title,
    actions: open ? /*#__PURE__*/React.createElement(Button, {
      variant: "secondary",
      onClick: () => setOpen(null)
    }, "\u2190 All sessions") : /*#__PURE__*/React.createElement(Button, {
      variant: "primary"
    }, "New pipeline")
  }, open ? /*#__PURE__*/React.createElement(LiveSessionView, {
    session: open,
    tick: tick
  }) : /*#__PURE__*/React.createElement(SessionsView, {
    sessions: SESSIONS,
    onOpen: s => {
      setOpen(s);
      setTick(0);
    }
  }));
}
ReactDOM.createRoot(document.getElementById('root')).render(/*#__PURE__*/React.createElement(App, null));
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/app.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/MetricsWorkbench.jsx
try { (() => {
// Voxa Studio — Run & Metrics workbench (VST-002 §9): percentile card,
// per-turn stage stacks with a plain-language takeaway, run list + compare.
const {
  Button: MwButton,
  Select: MwSelect,
  Card: MwCard,
  MetricStat: MwMetric
} = window.VOXADesignSystem_4f47fa;
const MW_TURNS = [{
  vad: 118,
  stt: 58,
  agent: 22,
  tts: 34,
  out: 8
}, {
  vad: 118,
  stt: 71,
  agent: 19,
  tts: 36,
  out: 5
}, {
  vad: 118,
  stt: 49,
  agent: 25,
  tts: 33,
  out: 6
}, {
  vad: 120,
  stt: 63,
  agent: 18,
  tts: 31,
  out: 7
}, {
  vad: 118,
  stt: 55,
  agent: 30,
  tts: 38,
  out: 6
}, {
  vad: 119,
  stt: 60,
  agent: 21,
  tts: 29,
  out: 5
}, {
  vad: 118,
  stt: 67,
  agent: 24,
  tts: 35,
  out: 8
}, {
  vad: 118,
  stt: 52,
  agent: 20,
  tts: 32,
  out: 6
}];
const MW_STAGES = [['vad', 'vad'], ['stt', 'stt'], ['agent', 'agent'], ['tts', 'tts'], ['out', 'out']];
const MW_RUNS = [{
  id: 14,
  cfg: 'piper',
  ttfb: 612,
  on: true
}, {
  id: 13,
  cfg: 'kokoro',
  ttfb: 884,
  on: true
}, {
  id: 12,
  cfg: 'base.en',
  ttfb: 698,
  on: false
}];
const MW_MAX = Math.max(...MW_TURNS.map(t => t.vad + t.stt + t.agent + t.tts + t.out));
function PctCard() {
  return /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 20,
      minWidth: 240
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Voice-to-voice TTFB"), /*#__PURE__*/React.createElement("div", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 40,
      fontWeight: 500,
      color: 'var(--text-1)',
      lineHeight: 1.1,
      marginTop: 12
    }
  }, "612", /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 14,
      color: 'var(--text-3)',
      marginLeft: 4
    }
  }, "ms p50")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 4,
      marginTop: 12,
      fontFamily: 'var(--font-mono)',
      fontSize: 14,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", null, "p95\xA0 884 ms"), /*#__PURE__*/React.createElement("span", null, "max 1041 ms")), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 16,
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--ok)'
    }
  }, "\u25BC 31% vs run #13 (kokoro)"));
}
function StageStacks() {
  return /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 20,
      flex: 1,
      minWidth: 360
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Stage breakdown per turn (ms)"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 7,
      marginTop: 16
    }
  }, MW_TURNS.map((t, i) => {
    const total = t.vad + t.stt + t.agent + t.tts + t.out;
    return /*#__PURE__*/React.createElement("div", {
      key: i,
      style: {
        display: 'flex',
        alignItems: 'center',
        gap: 10
      }
    }, /*#__PURE__*/React.createElement("span", {
      style: {
        fontFamily: 'var(--font-mono)',
        fontSize: 10,
        color: 'var(--text-muted)',
        width: 18
      }
    }, i + 1), /*#__PURE__*/React.createElement("div", {
      style: {
        flex: 1,
        display: 'flex',
        height: 13,
        borderRadius: 3,
        overflow: 'hidden',
        background: 'var(--surface-3)'
      }
    }, MW_STAGES.map(([s]) => /*#__PURE__*/React.createElement("div", {
      key: s,
      title: `${s} ${t[s]}ms`,
      style: {
        width: `${t[s] / MW_MAX * 100}%`,
        background: `var(--stage-${s})`
      }
    }))), /*#__PURE__*/React.createElement("span", {
      style: {
        fontFamily: 'var(--font-mono)',
        fontSize: 10,
        color: 'var(--text-3)',
        width: 30,
        textAlign: 'right'
      }
    }, total));
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      marginTop: 14,
      flexWrap: 'wrap'
    }
  }, MW_STAGES.map(([s, l]) => /*#__PURE__*/React.createElement("span", {
    key: s,
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 6,
      fontFamily: 'var(--font-mono)',
      fontSize: 10,
      color: 'var(--text-3)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 8,
      height: 8,
      borderRadius: '50%',
      background: `var(--stage-${s})`
    }
  }), l))));
}
function RunList() {
  return /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 18,
      width: 190,
      flex: 'none'
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Runs"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 10,
      marginTop: 14
    }
  }, MW_RUNS.map(r => /*#__PURE__*/React.createElement("label", {
    key: r.id,
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 8,
      fontSize: 12.5,
      color: r.on ? 'var(--text-1)' : 'var(--text-3)',
      cursor: 'pointer'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 13,
      height: 13,
      borderRadius: 3,
      flex: 'none',
      border: '1px solid var(--line-3)',
      background: r.on ? 'var(--pulse-400)' : 'transparent'
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      flex: 1
    }
  }, "#", r.id, " ", r.cfg), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, r.ttfb, "ms")))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 8,
      marginTop: 16,
      fontSize: 12
    }
  }, /*#__PURE__*/React.createElement("a", {
    href: "#",
    onClick: e => e.preventDefault(),
    style: {
      color: 'var(--pulse-400)'
    }
  }, "\u21C4 compare selected"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    onClick: e => e.preventDefault(),
    style: {
      color: 'var(--text-3)'
    }
  }, "\u2913 export CSV / JSON")));
}
function MetricsWorkbench() {
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "Metrics",
    sub: "run #14 \xB7 whispercpp\xB7echo\xB7piper \xB7 scripted-8-utterances"
  }, /*#__PURE__*/React.createElement(MwSelect, {
    options: ['scripted · 8 utterances', 'live mic', 'jfk.wav fixture'],
    defaultValue: "scripted \xB7 8 utterances",
    style: {
      width: 200
    }
  }), /*#__PURE__*/React.createElement(MwButton, {
    variant: "primary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "play",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Run")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 20
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      alignItems: 'stretch',
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement(PctCard, null), /*#__PURE__*/React.createElement(StageStacks, null), /*#__PURE__*/React.createElement(RunList, null)), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 16,
      padding: '14px 18px',
      borderRadius: 'var(--r-lg)',
      border: '1px solid var(--line-1)',
      borderLeft: '3px solid var(--warn)',
      background: 'var(--surface-1)',
      display: 'flex',
      alignItems: 'center',
      gap: 10
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": "lightbulb",
    style: {
      width: 16,
      height: 16,
      color: 'var(--warn)',
      flex: 'none'
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 13,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("b", {
    style: {
      color: 'var(--text-1)'
    }
  }, "Takeaway:"), " VAD hangover is 54% of p50 \u2014 lower ", /*#__PURE__*/React.createElement("code", {
    style: {
      fontFamily: 'var(--font-mono)',
      color: 'var(--pulse-300)'
    }
  }, "StopDuration"), " or enable smart-turn.")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(4, 1fr)',
      gap: 16,
      marginTop: 16
    }
  }, /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 18
    }
  }, /*#__PURE__*/React.createElement(MwMetric, {
    label: "RTF \xB7 TTS leg",
    value: "0.28",
    delta: "\u25BC 0.04 vs #13",
    deltaTone: "good"
  })), /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 18
    }
  }, /*#__PURE__*/React.createElement(MwMetric, {
    label: "Turns",
    value: "8",
    delta: "scripted",
    deltaTone: "flat"
  })), /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 18
    }
  }, /*#__PURE__*/React.createElement(MwMetric, {
    label: "Interruptions",
    value: "2",
    delta: "barge-in",
    deltaTone: "flat"
  })), /*#__PURE__*/React.createElement(MwCard, {
    style: {
      padding: 18
    }
  }, /*#__PURE__*/React.createElement(MwMetric, {
    label: "Errors",
    value: "0",
    delta: "clean run",
    deltaTone: "good"
  })))));
}
window.MetricsWorkbench = MetricsWorkbench;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/MetricsWorkbench.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/PipelineBuilder.jsx
try { (() => {
// Voxa Studio — Pipeline Builder (VST-002 §8): palette → canvas → inspector,
// run-from-canvas with live edge flow + node glow + bottom turn ticker.
const {
  Button: VxButton,
  Select: VxSelect,
  Badge: VxBadge
} = window.VOXADesignSystem_4f47fa;
const NODE_PARAMS = {
  mic: [{
    k: 'Device',
    type: 'select',
    opts: ['Default · WASAPI', 'USB Mic'],
    v: 'Default · WASAPI'
  }, {
    k: 'Sample rate',
    type: 'select',
    opts: ['16 kHz', '48 kHz'],
    v: '16 kHz'
  }],
  vad: [{
    k: 'ConfidenceThreshold',
    type: 'range',
    min: 0,
    max: 1,
    step: 0.01,
    v: 0.5
  }, {
    k: 'StopDuration',
    type: 'range',
    min: 200,
    max: 1500,
    step: 50,
    v: 800,
    unit: 'ms'
  }],
  stt: [{
    k: 'Model',
    type: 'select',
    opts: ['tiny.en', 'base.en', 'small.en'],
    v: 'tiny.en'
  }, {
    k: 'Language',
    type: 'select',
    opts: ['en', 'auto'],
    v: 'en'
  }, {
    k: 'BeamSize',
    type: 'range',
    min: 1,
    max: 8,
    step: 1,
    v: 5
  }],
  agent: [{
    k: 'Model',
    type: 'select',
    opts: ['gpt-4o-mini', 'gpt-4o'],
    v: 'gpt-4o-mini'
  }, {
    k: 'Temperature',
    type: 'range',
    min: 0,
    max: 1,
    step: 0.05,
    v: 0.7
  }],
  tts: [{
    k: 'Voice',
    type: 'select',
    opts: ['amy-low', 'amy-medium', 'ryan-high'],
    v: 'amy-low'
  }, {
    k: 'Speed',
    type: 'range',
    min: 0.5,
    max: 1.5,
    step: 0.05,
    v: 1.0,
    unit: '×'
  }],
  speaker: [{
    k: 'Device',
    type: 'select',
    opts: ['Default output', 'Headphones'],
    v: 'Default output'
  }]
};
function Slider({
  spec
}) {
  const [v, setV] = React.useState(spec.v);
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 6
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      alignItems: 'baseline'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, spec.k), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--pulse-300)'
    }
  }, v, spec.unit || '')), /*#__PURE__*/React.createElement("input", {
    className: "vxb-range",
    type: "range",
    min: spec.min,
    max: spec.max,
    step: spec.step,
    value: v,
    onChange: e => setV(parseFloat(e.target.value))
  }));
}
function Inspector({
  nodeId
}) {
  const node = window.STUDIO_NODES.find(n => n.id === nodeId);
  const specs = NODE_PARAMS[nodeId] || [];
  const accent = `var(--stage-${node.stage})`;
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Inspector \xB7 ", node.kind), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 8,
      marginTop: 8
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 9,
      height: 9,
      borderRadius: '50%',
      background: accent
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 15,
      fontWeight: 600
    }
  }, node.name), node.cached && /*#__PURE__*/React.createElement(VxBadge, {
    tone: "ok"
  }, "cached"))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, specs.map(s => s.type === 'range' ? /*#__PURE__*/React.createElement(Slider, {
    key: s.k,
    spec: s
  }) : /*#__PURE__*/React.createElement(VxSelect, {
    key: s.k,
    label: s.k,
    options: s.opts,
    defaultValue: s.v
  }))), /*#__PURE__*/React.createElement("div", {
    style: {
      borderTop: '1px solid var(--line-1)',
      paddingTop: 14,
      display: 'flex',
      flexDirection: 'column',
      gap: 6
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label"
  }, "Frame types"), /*#__PURE__*/React.createElement("div", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, "in\xA0 ", node.inType ? window.FRAME_LABEL[node.inType] : '—', /*#__PURE__*/React.createElement("br", null), "out ", node.outType ? window.FRAME_LABEL[node.outType] : '—')));
}
function TurnTicker() {
  const seg = [['vad', 118], ['stt', 58], ['agent', 22], ['tts', 34], ['out', 8]];
  const total = seg.reduce((a, [, v]) => a + v, 0);
  return /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'absolute',
      left: 16,
      right: 16,
      bottom: 12,
      height: 40,
      borderRadius: 'var(--r-md)',
      background: 'var(--glass-bg)',
      backdropFilter: 'var(--blur-glass)',
      border: '1px solid var(--line-1)',
      display: 'flex',
      alignItems: 'center',
      gap: 12,
      padding: '0 14px'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      flex: 'none'
    }
  }, "last turn"), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      height: 12,
      borderRadius: 3,
      overflow: 'hidden'
    }
  }, seg.map(([s, v]) => /*#__PURE__*/React.createElement("div", {
    key: s,
    title: `${s} ${v}ms`,
    style: {
      width: `${v / total * 100}%`,
      background: `var(--stage-${s})`
    }
  }))), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--text-1)'
    }
  }, total, " ms"));
}
function PipelineBuilder() {
  const [sel, setSel] = React.useState('vad');
  const [live, setLive] = React.useState(false);
  const [activeIdx, setActiveIdx] = React.useState(0);
  const order = window.STUDIO_NODES.map(n => n.id);
  React.useEffect(() => {
    if (!live) return;
    const t = setInterval(() => setActiveIdx(i => (i + 1) % order.length), 520);
    return () => clearInterval(t);
  }, [live]);
  const activeId = live ? order[activeIdx] : null;
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement("style", null, `
        .vxb-range { -webkit-appearance: none; appearance: none; width: 100%; height: 4px; border-radius: 999px;
          background: var(--surface-4); outline: none; }
        .vxb-range::-webkit-slider-thumb { -webkit-appearance: none; width: 15px; height: 15px; border-radius: 50%;
          background: var(--pulse-400); border: 2px solid var(--bg-page); cursor: pointer; box-shadow: 0 0 0 1px var(--pulse-500); }
        .vxb-range::-moz-range-thumb { width: 13px; height: 13px; border-radius: 50%; background: var(--pulse-400); border: 2px solid var(--bg-page); cursor: pointer; }
      `), /*#__PURE__*/React.createElement(ViewBar, {
    title: "Builder",
    sub: "wire the chain \xB7 ports accept matching frame types"
  }, /*#__PURE__*/React.createElement(VxButton, {
    variant: "secondary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "file-down",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "appsettings"), /*#__PURE__*/React.createElement(VxButton, {
    variant: "secondary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "braces",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "C# compose"), /*#__PURE__*/React.createElement(VxButton, {
    variant: live ? 'danger' : 'primary',
    size: "sm",
    onClick: () => {
      setLive(x => !x);
      setActiveIdx(0);
    },
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": live ? 'square' : 'play',
      style: {
        width: 14,
        height: 14
      }
    })
  }, live ? 'Stop' : 'Run graph')), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      minHeight: 0
    }
  }, /*#__PURE__*/React.createElement("aside", {
    style: {
      width: 188,
      flex: 'none',
      borderRight: '1px solid var(--line-1)',
      padding: 16,
      overflowY: 'auto',
      background: 'var(--bg-panel)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      marginBottom: 12
    }
  }, "Node palette"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 6
    }
  }, window.PALETTE.map(p => /*#__PURE__*/React.createElement("div", {
    key: p.label,
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 9,
      padding: '8px 10px',
      borderRadius: 'var(--r-sm)',
      border: '1px solid var(--line-1)',
      background: 'var(--surface-2)',
      cursor: 'grab',
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 8,
      height: 8,
      borderRadius: '50%',
      flex: 'none',
      background: window.FRAME_COLORS[p.type]
    }
  }), p.label))), /*#__PURE__*/React.createElement("p", {
    style: {
      marginTop: 14,
      fontSize: 11,
      color: 'var(--text-muted)',
      lineHeight: 1.5
    }
  }, "Drag onto the canvas, or use a port's ", /*#__PURE__*/React.createElement("b", {
    style: {
      color: 'var(--text-3)'
    }
  }, "+"), " to add only type-compatible nodes.")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      position: 'relative',
      overflow: 'auto',
      background: 'var(--bg-page)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'relative',
      width: window.CANVAS_W,
      height: window.CANVAS_H,
      margin: '28px 24px',
      backgroundImage: 'linear-gradient(var(--line-1) 1px, transparent 1px), linear-gradient(90deg, var(--line-1) 1px, transparent 1px)',
      backgroundSize: '40px 40px'
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      position: 'absolute',
      top: -22,
      left: 0
    }
  }, "Builder canvas \xB7 single-in / single-out"), /*#__PURE__*/React.createElement(window.CanvasEdges, {
    active: live
  }), window.STUDIO_NODES.map(n => /*#__PURE__*/React.createElement(window.CanvasNode, {
    key: n.id,
    node: n,
    selected: sel === n.id,
    active: activeId === n.id,
    onSelect: setSel
  })), live && /*#__PURE__*/React.createElement(TurnTicker, null))), /*#__PURE__*/React.createElement("aside", {
    style: {
      width: 264,
      flex: 'none',
      borderLeft: '1px solid var(--line-1)',
      padding: 18,
      overflowY: 'auto',
      background: 'var(--bg-panel)'
    }
  }, /*#__PURE__*/React.createElement(Inspector, {
    nodeId: sel
  }))));
}
window.PipelineBuilder = PipelineBuilder;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/PipelineBuilder.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/Splash.jsx
try { (() => {
// Voxa Studio — animated launch splash (480×320 borderless window per VST-002 §4).
// Mark draws on, three signal bars spring up, wordmark settles, microcopy ticks.
// Honors prefers-reduced-motion. Click to skip.
//
// Static-capture safe: the visible END state is set as INLINE styles (always
// captured by static renderers); the intro animation is layered via the
// `.vxs-play` class, which overrides during playback and is removed when done.
const SPLASH_STAGES = ['configuration', 'providers', 'devices', 'model cache', 'ready'];
function SplashStyles() {
  return /*#__PURE__*/React.createElement("style", null, `
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
    `);
}
function Splash({
  onSkip
}) {
  const ref = React.useRef(null);
  const [stage, setStage] = React.useState(0);
  React.useEffect(() => {
    const t = setInterval(() => setStage(s => Math.min(s + 1, SPLASH_STAGES.length - 1)), 420);
    return () => clearInterval(t);
  }, []);
  React.useEffect(() => {
    const el = ref.current;
    if (!el || window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    el.classList.add('vxs-play');
    const t = setTimeout(() => el && el.classList.remove('vxs-play'), 2500);
    return () => clearTimeout(t);
  }, []);
  const stroke = {
    fill: 'none',
    stroke: 'var(--pulse-400)',
    strokeWidth: 7,
    strokeLinecap: 'round',
    strokeLinejoin: 'round'
  };
  return /*#__PURE__*/React.createElement("div", {
    ref: ref,
    className: "vxs-stage",
    onClick: onSkip,
    role: "img",
    "aria-label": "Voxa Studio launching"
  }, /*#__PURE__*/React.createElement(SplashStyles, null), /*#__PURE__*/React.createElement("svg", {
    className: "vxs-mark",
    viewBox: "0 0 100 100"
  }, /*#__PURE__*/React.createElement("path", {
    className: "glow",
    d: "M14,18 L50,86 L86,18",
    style: {
      ...stroke,
      strokeWidth: 13,
      opacity: 0,
      filter: 'blur(9px)'
    }
  }), /*#__PURE__*/React.createElement("path", {
    className: "v",
    d: "M14,18 L50,86 L86,18",
    style: {
      ...stroke,
      strokeDasharray: 160,
      strokeDashoffset: 0
    }
  }), /*#__PURE__*/React.createElement("rect", {
    className: "bar b1",
    x: "38",
    y: "34",
    width: "7",
    height: "22",
    rx: "3.5",
    style: {
      fill: 'var(--pulse-400)',
      opacity: 1,
      transformOrigin: 'center bottom'
    }
  }), /*#__PURE__*/React.createElement("rect", {
    className: "bar b2",
    x: "50",
    y: "26",
    width: "7",
    height: "30",
    rx: "3.5",
    style: {
      fill: 'var(--pulse-400)',
      opacity: 1,
      transformOrigin: 'center bottom'
    }
  }), /*#__PURE__*/React.createElement("rect", {
    className: "bar b3",
    x: "62",
    y: "38",
    width: "7",
    height: "18",
    rx: "3.5",
    style: {
      fill: 'var(--pulse-400)',
      opacity: 1,
      transformOrigin: 'center bottom'
    }
  })), /*#__PURE__*/React.createElement("div", {
    className: "vxs-word",
    style: {
      fontFamily: 'var(--font-ui)',
      fontWeight: 700,
      fontSize: 23,
      letterSpacing: '0.42em',
      color: 'var(--text-1)',
      opacity: 1,
      paddingLeft: '0.42em'
    }
  }, "VOXA ", /*#__PURE__*/React.createElement("b", {
    style: {
      fontWeight: 400,
      color: 'var(--text-3)'
    }
  }, "STUDIO")), /*#__PURE__*/React.createElement("div", {
    className: "vxs-meta",
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      letterSpacing: '0.06em',
      color: 'var(--text-muted)',
      opacity: 1,
      display: 'flex',
      alignItems: 'center',
      gap: 8
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 5,
      height: 5,
      borderRadius: '50%',
      background: 'var(--pulse-400)',
      boxShadow: '0 0 8px var(--pulse-400)'
    }
  }), SPLASH_STAGES[stage], stage < SPLASH_STAGES.length - 1 ? '…' : ' · click to enter'), /*#__PURE__*/React.createElement("div", {
    className: "vxs-bottom",
    style: {
      position: 'absolute',
      left: 0,
      right: 0,
      bottom: 0,
      height: 3,
      background: 'var(--pulse-400)',
      transformOrigin: 'left',
      transform: 'scaleX(1)',
      opacity: 0.85
    }
  }));
}
window.Splash = Splash;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/Splash.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/SttPlayground.jsx
try { (() => {
// Voxa Studio — STT Playground (VST-002 §6): how well & how fast does speech
// become text on this machine, for any Whisper model, without a whole pipeline.
const {
  Button: StButton,
  Select: StSelect,
  Badge: StBadge,
  Switch: StSwitch,
  Waveform: StWave
} = window.VOXADesignSystem_4f47fa;
const STT_SOURCES = [{
  id: 'mic',
  icon: 'mic',
  label: 'Live mic'
}, {
  id: 'file',
  icon: 'file-audio',
  label: 'Drop WAV'
}, {
  id: 'fixture',
  icon: 'repeat',
  label: 'Fixtures'
}];
const STT_CARDS = [{
  text: "What's the status of order four four two one?",
  dur: '2.1s',
  latency: 142,
  levels: [.3, .6, .8, .5, .9, .7, .4, .6, .3, .7, .5, .2, .6, .8, .4]
}, {
  text: 'Can you ship it express instead?',
  dur: '1.6s',
  latency: 118,
  levels: [.4, .7, .5, .9, .6, .3, .7, .5, .8, .4, .6, .3]
}];
// WER diff: reference vs hypothesis token ops.
const WER_DIFF = [{
  w: 'the',
  op: 'ok'
}, {
  w: 'quick',
  op: 'ok'
}, {
  w: 'brown',
  op: 'ok'
}, {
  w: 'fox',
  op: 'ok'
}, {
  w: 'jumped',
  op: 'sub',
  was: 'jumps'
}, {
  w: 'over',
  op: 'ok'
}, {
  w: 'a',
  op: 'sub',
  was: 'the'
}, {
  w: 'lazy',
  op: 'ok'
}, {
  w: 'dog',
  op: 'ok'
}, {
  w: 'today',
  op: 'ins'
}];
function SttPlayground() {
  const [src, setSrc] = React.useState('fixture');
  const [side, setSide] = React.useState(false);
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "STT Playground",
    sub: "speech \u2192 text \xB7 standalone, measured on this machine"
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 8
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label"
  }, "side-by-side"), /*#__PURE__*/React.createElement(StSwitch, {
    checked: side,
    onChange: setSide
  }))), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 20,
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 16,
      padding: 14,
      borderRadius: 'var(--r-lg)',
      border: '1px solid var(--line-1)',
      background: 'var(--surface-1)',
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 4,
      padding: 3,
      borderRadius: 'var(--r-md)',
      background: 'var(--surface-3)'
    }
  }, STT_SOURCES.map(s => /*#__PURE__*/React.createElement("button", {
    key: s.id,
    onClick: () => setSrc(s.id),
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 7,
      padding: '7px 12px',
      borderRadius: 'var(--r-sm)',
      border: 'none',
      cursor: 'pointer',
      background: src === s.id ? 'var(--pulse-400)' : 'transparent',
      color: src === s.id ? 'var(--text-on-pulse)' : 'var(--text-2)',
      fontFamily: 'var(--font-ui)',
      fontSize: 12.5,
      fontWeight: 600
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": s.icon,
    style: {
      width: 14,
      height: 14
    }
  }), s.label))), /*#__PURE__*/React.createElement(StSelect, {
    options: ['tiny.en', 'base.en', 'small.en', 'tiny.en · q5_1'],
    defaultValue: "tiny.en",
    style: {
      width: 150
    }
  }), /*#__PURE__*/React.createElement(StBadge, {
    tone: "neutral"
  }, "75 MB"), /*#__PURE__*/React.createElement(StBadge, {
    tone: "ok"
  }, "cached"), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1
    }
  }), /*#__PURE__*/React.createElement(StButton, {
    variant: "primary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "play",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Transcribe jfk.wav")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      alignItems: 'flex-start',
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      minWidth: 320,
      display: 'flex',
      flexDirection: 'column',
      gap: 12
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label"
  }, "Transcript \xB7 final cards"), STT_CARDS.map((c, i) => /*#__PURE__*/React.createElement("div", {
    key: i,
    style: {
      padding: 14,
      borderRadius: 'var(--r-lg)',
      border: '1px solid var(--line-1)',
      background: 'var(--surface-card)',
      boxShadow: 'var(--shadow-1), var(--edge-light)'
    }
  }, /*#__PURE__*/React.createElement(StWave, {
    bars: c.levels.length,
    levels: c.levels,
    height: 26,
    style: {
      marginBottom: 10
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      fontSize: 14,
      color: 'var(--text-1)'
    }
  }, c.text), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 14,
      marginTop: 8,
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, /*#__PURE__*/React.createElement("span", null, "utterance ", c.dur), /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--pulse-300)'
    }
  }, "final +", c.latency, " ms")))), side && /*#__PURE__*/React.createElement("div", {
    style: {
      padding: 14,
      borderRadius: 'var(--r-lg)',
      border: '1px dashed var(--line-2)',
      color: 'var(--text-3)',
      fontSize: 12.5
    }
  }, "base.en \xB7 same audio \u2192 ", /*#__PURE__*/React.createElement("b", {
    style: {
      color: 'var(--text-2)'
    }
  }, "4.9% WER"), ", 240 ms slower. One whisper context at a time.")), /*#__PURE__*/React.createElement("div", {
    style: {
      width: 340,
      flex: 'none',
      padding: 16,
      borderRadius: 'var(--r-lg)',
      border: '1px solid var(--line-1)',
      background: 'var(--surface-1)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Accuracy harness \xB7 WER"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'baseline',
      gap: 10,
      margin: '12px 0 14px'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 34,
      fontWeight: 500,
      color: 'var(--text-1)'
    }
  }, "8.1", /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 15,
      color: 'var(--text-3)'
    }
  }, "%")), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "1 sub \xB7 1 ins \xB7 0 del")), /*#__PURE__*/React.createElement("div", {
    style: {
      lineHeight: 1.9,
      fontSize: 13.5
    }
  }, WER_DIFF.map((t, i) => {
    const st = t.op === 'ok' ? {
      color: 'var(--text-2)'
    } : t.op === 'sub' ? {
      color: 'var(--warn)',
      background: 'var(--warn-soft)',
      borderRadius: 3,
      padding: '1px 4px'
    } : {
      color: 'var(--info)',
      background: 'var(--info-soft)',
      borderRadius: 3,
      padding: '1px 4px'
    };
    return /*#__PURE__*/React.createElement("span", {
      key: i
    }, /*#__PURE__*/React.createElement("span", {
      style: st,
      title: t.was ? `was "${t.was}"` : t.op === 'ins' ? 'inserted' : ''
    }, t.w), ' ');
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 14,
      marginTop: 16,
      fontSize: 11,
      color: 'var(--text-muted)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 5
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 8,
      height: 8,
      borderRadius: 2,
      background: 'var(--warn)'
    }
  }), "substitution"), /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 5
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 8,
      height: 8,
      borderRadius: 2,
      background: 'var(--info)'
    }
  }), "insertion"))))));
}
window.SttPlayground = SttPlayground;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/SttPlayground.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/StudioShell.jsx
try { (() => {
// Voxa Studio desktop shell — custom titlebar + icon nav rail + view host.
const {
  IconButton
} = window.VOXADesignSystem_4f47fa;
const ASSET_BASE = '../..';
const STUDIO_NAV = [{
  id: 'talk',
  icon: 'mic',
  label: 'Talk'
}, {
  id: 'playgrounds',
  icon: 'flask-conical',
  label: 'Playgrounds'
}, {
  id: 'builder',
  icon: 'workflow',
  label: 'Builder'
}, {
  id: 'metrics',
  icon: 'bar-chart-3',
  label: 'Metrics'
}, {
  id: 'models',
  icon: 'box',
  label: 'Models'
}, {
  id: 'config',
  icon: 'settings-2',
  label: 'Config'
}];
function WinDot({
  icon,
  danger
}) {
  return /*#__PURE__*/React.createElement("button", {
    style: {
      width: 38,
      height: 30,
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center',
      background: 'transparent',
      border: 'none',
      color: 'var(--text-3)',
      cursor: 'default',
      borderRadius: 6,
      transition: 'background var(--dur-fast) var(--ease-out), color var(--dur-fast) var(--ease-out)'
    },
    onMouseEnter: e => {
      e.currentTarget.style.background = danger ? 'var(--danger)' : 'var(--surface-3)';
      e.currentTarget.style.color = danger ? '#fff' : 'var(--text-1)';
    },
    onMouseLeave: e => {
      e.currentTarget.style.background = 'transparent';
      e.currentTarget.style.color = 'var(--text-3)';
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": icon,
    style: {
      width: 14,
      height: 14
    }
  }));
}
function StudioShell({
  nav,
  onNav,
  live,
  sessionTime,
  onReplaySplash,
  children
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      height: '100vh',
      display: 'flex',
      flexDirection: 'column',
      overflow: 'hidden',
      background: 'var(--bg-page)',
      border: '1px solid var(--line-2)'
    }
  }, /*#__PURE__*/React.createElement("header", {
    style: {
      height: 44,
      flex: 'none',
      display: 'flex',
      alignItems: 'center',
      gap: 12,
      padding: '0 6px 0 14px',
      background: 'var(--bg-panel)',
      borderBottom: '1px solid var(--line-1)',
      userSelect: 'none'
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: `${ASSET_BASE}/assets/voxa-mark.svg`,
    alt: "",
    style: {
      height: 18
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-ui)',
      fontWeight: 700,
      fontSize: 12,
      letterSpacing: '0.28em',
      color: 'var(--text-1)'
    }
  }, "VOXA ", /*#__PURE__*/React.createElement("span", {
    style: {
      fontWeight: 400,
      color: 'var(--text-3)'
    }
  }, "STUDIO")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1
    }
  }), live && /*#__PURE__*/React.createElement("div", {
    className: "vx-status vx-status--live",
    style: {
      marginRight: 4
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-status__dot"
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)'
    }
  }, sessionTime)), /*#__PURE__*/React.createElement("button", {
    onClick: onReplaySplash,
    title: "Replay launch",
    style: {
      width: 30,
      height: 30,
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center',
      background: 'transparent',
      border: 'none',
      color: 'var(--text-3)',
      cursor: 'pointer',
      borderRadius: 6
    },
    onMouseEnter: e => {
      e.currentTarget.style.color = 'var(--pulse-400)';
    },
    onMouseLeave: e => {
      e.currentTarget.style.color = 'var(--text-3)';
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": "sparkles",
    style: {
      width: 15,
      height: 15
    }
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      width: 1,
      height: 18,
      background: 'var(--line-2)',
      margin: '0 2px'
    }
  }), /*#__PURE__*/React.createElement(WinDot, {
    icon: "minus"
  }), /*#__PURE__*/React.createElement(WinDot, {
    icon: "square"
  }), /*#__PURE__*/React.createElement(WinDot, {
    icon: "x",
    danger: true
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      minHeight: 0
    }
  }, /*#__PURE__*/React.createElement("nav", {
    style: {
      width: 82,
      flex: 'none',
      background: 'var(--bg-panel)',
      borderRight: '1px solid var(--line-1)',
      display: 'flex',
      flexDirection: 'column',
      padding: '8px 0',
      gap: 2
    }
  }, STUDIO_NAV.map(n => {
    const on = nav === n.id;
    return /*#__PURE__*/React.createElement("button", {
      key: n.id,
      onClick: () => onNav(n.id),
      title: n.label,
      style: {
        position: 'relative',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        gap: 5,
        padding: '11px 4px',
        margin: '0 6px',
        background: on ? 'var(--surface-2)' : 'transparent',
        border: '1px solid',
        borderColor: on ? 'var(--line-1)' : 'transparent',
        borderRadius: 'var(--r-md)',
        color: on ? 'var(--pulse-400)' : 'var(--text-3)',
        cursor: 'pointer',
        transition: 'background var(--dur-fast) var(--ease-out), color var(--dur-fast) var(--ease-out)'
      },
      onMouseEnter: e => {
        if (!on) e.currentTarget.style.color = 'var(--text-1)';
      },
      onMouseLeave: e => {
        if (!on) e.currentTarget.style.color = 'var(--text-3)';
      }
    }, on && /*#__PURE__*/React.createElement("span", {
      style: {
        position: 'absolute',
        left: -6,
        top: 12,
        bottom: 12,
        width: 2,
        borderRadius: 2,
        background: 'var(--pulse-400)'
      }
    }), /*#__PURE__*/React.createElement("i", {
      "data-lucide": n.icon,
      style: {
        width: 20,
        height: 20
      }
    }), /*#__PURE__*/React.createElement("span", {
      style: {
        fontSize: 10,
        fontWeight: 500,
        letterSpacing: '0.01em'
      }
    }, n.label));
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 'auto',
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      gap: 8,
      padding: '8px 0'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 28,
      height: 28,
      borderRadius: '50%',
      background: 'var(--surface-3)',
      border: '1px solid var(--line-2)',
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center',
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-2)'
    }
  }, "MK"))), /*#__PURE__*/React.createElement("main", {
    style: {
      flex: 1,
      minWidth: 0,
      display: 'flex',
      flexDirection: 'column',
      overflow: 'hidden'
    }
  }, children)));
}

// Shared view chrome: a toolbar header used by every view for a consistent desktop feel.
function ViewBar({
  title,
  sub,
  children
}) {
  return /*#__PURE__*/React.createElement("header", {
    style: {
      flex: 'none',
      display: 'flex',
      alignItems: 'center',
      gap: 14,
      padding: '12px 20px',
      borderBottom: '1px solid var(--line-1)',
      background: 'var(--bg-panel)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      minWidth: 0
    }
  }, /*#__PURE__*/React.createElement("h1", {
    style: {
      fontSize: 18,
      fontWeight: 700,
      letterSpacing: '-0.01em',
      whiteSpace: 'nowrap'
    }
  }, title), sub && /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      marginTop: 3
    }
  }, sub)), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 10
    }
  }, children));
}
window.StudioShell = StudioShell;
window.ViewBar = ViewBar;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/StudioShell.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/SupportingViews.jsx
try { (() => {
// Voxa Studio — supporting views: Playgrounds wrapper (STT lab | TTS lab),
// Models cache manager, Config composer (v1, gains "open in Builder").
const {
  Button: SvButton,
  Badge: SvBadge,
  Card: SvCard,
  Select: SvSelect
} = window.VOXADesignSystem_4f47fa;
function PlaygroundsView() {
  const [lab, setLab] = React.useState('stt');
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 'none',
      display: 'flex',
      gap: 4,
      padding: '8px 14px',
      borderBottom: '1px solid var(--line-1)',
      background: 'var(--bg-panel)'
    }
  }, [['stt', 'STT lab'], ['tts', 'TTS lab']].map(([id, label]) => /*#__PURE__*/React.createElement("button", {
    key: id,
    onClick: () => setLab(id),
    style: {
      padding: '6px 14px',
      borderRadius: 'var(--r-sm)',
      border: '1px solid',
      cursor: 'pointer',
      borderColor: lab === id ? 'var(--line-2)' : 'transparent',
      background: lab === id ? 'var(--surface-3)' : 'transparent',
      color: lab === id ? 'var(--text-1)' : 'var(--text-3)',
      fontFamily: 'var(--font-ui)',
      fontSize: 12.5,
      fontWeight: 600
    }
  }, label))), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      flexDirection: 'column',
      minHeight: 0
    }
  }, lab === 'stt' ? /*#__PURE__*/React.createElement(window.SttPlayground, null) : /*#__PURE__*/React.createElement(window.TtsPlayground, null)));
}
const MODELS = [{
  name: 'whisper · tiny.en',
  size: '75 MB',
  state: 'cached'
}, {
  name: 'whisper · base.en',
  size: '142 MB',
  state: 'cached'
}, {
  name: 'whisper · small.en',
  size: '466 MB',
  state: 'available'
}, {
  name: 'piper · amy-low',
  size: '63 MB',
  state: 'cached'
}, {
  name: 'kokoro · v0.19',
  size: '310 MB',
  state: 'cached'
}, {
  name: 'silero-vad',
  size: '2 MB',
  state: 'cached'
}];
function ModelsView() {
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "Models",
    sub: "local cache \xB7 nothing leaves the machine"
  }, /*#__PURE__*/React.createElement(SvButton, {
    variant: "secondary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "refresh-cw",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Rescan")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 20
    }
  }, /*#__PURE__*/React.createElement(SvCard, {
    style: {
      padding: 6
    }
  }, MODELS.map((m, i) => /*#__PURE__*/React.createElement("div", {
    key: m.name,
    style: {
      display: 'grid',
      gridTemplateColumns: '1fr auto auto',
      alignItems: 'center',
      gap: 16,
      padding: '13px 16px',
      borderTop: i ? '1px solid var(--line-1)' : 'none'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 10
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": "box",
    style: {
      width: 16,
      height: 16,
      color: 'var(--text-3)'
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 13.5,
      color: 'var(--text-1)'
    }
  }, m.name)), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, m.size), m.state === 'cached' ? /*#__PURE__*/React.createElement(SvBadge, {
    tone: "ok"
  }, "cached") : /*#__PURE__*/React.createElement(SvButton, {
    variant: "secondary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "download",
      style: {
        width: 13,
        height: 13
      }
    })
  }, "Download"))))));
}
const CONFIG_STAGES = [{
  stage: 'vad',
  label: 'Transport',
  opts: ['WASAPI mic · 16 kHz']
}, {
  stage: 'vad',
  label: 'VAD',
  opts: ['Silero', 'WebRTC']
}, {
  stage: 'stt',
  label: 'STT',
  opts: ['WhisperCpp · tiny.en', 'WhisperCpp · base.en']
}, {
  stage: 'agent',
  label: 'Agent',
  opts: ['OpenAI · gpt-4o-mini', 'OpenAI · gpt-4o']
}, {
  stage: 'tts',
  label: 'TTS',
  opts: ['Piper · amy-low', 'Kokoro · af_sky']
}, {
  stage: 'out',
  label: 'Sink',
  opts: ['Default output device']
}];
function ConfigView({
  onOpenBuilder
}) {
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "Config",
    sub: "the default composer \xB7 appsettings-shaped"
  }, /*#__PURE__*/React.createElement(SvButton, {
    variant: "secondary",
    size: "sm",
    onClick: onOpenBuilder,
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "workflow",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Open as graph")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 20
    }
  }, /*#__PURE__*/React.createElement(SvCard, {
    style: {
      padding: 18,
      maxWidth: 640
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 14
    }
  }, CONFIG_STAGES.map(s => /*#__PURE__*/React.createElement("div", {
    key: s.label,
    style: {
      display: 'grid',
      gridTemplateColumns: '120px 1fr',
      alignItems: 'center',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 9
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 9,
      height: 9,
      borderRadius: '50%',
      background: `var(--stage-${s.stage})`
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 13,
      color: 'var(--text-2)'
    }
  }, s.label)), /*#__PURE__*/React.createElement(SvSelect, {
    options: s.opts,
    defaultValue: s.opts[0]
  })))))));
}
Object.assign(window, {
  PlaygroundsView,
  ModelsView,
  ConfigView
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/SupportingViews.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/TalkView.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
// Voxa Studio — Talk (the v1 conversation view; role unchanged). Watch the
// pipeline think: orb + live waveform, streaming transcript, running chain.
const {
  TranscriptLine: TkLine,
  VoiceOrb: TkOrb,
  Waveform: TkWave,
  PipelineFlow: TkFlow,
  StatusPill: TkPill,
  Button: TkButton
} = window.VOXADesignSystem_4f47fa;
const TALK_LINES = [{
  role: 'agent',
  text: 'Order 4421 shipped this morning via FedEx —',
  time: '00:16.040'
}, {
  role: 'user',
  text: 'Oh wait, sorry —',
  time: '00:17.610'
}, {
  role: 'system',
  text: 'frame: interruption · agent yielded in 96 ms',
  time: '00:17.702'
}, {
  role: 'user',
  text: 'can you send it to my office address instead?',
  time: '00:19.180'
}, {
  role: 'agent',
  text: 'Sure — updating the shipment to your office now.',
  time: '00:20.450',
  partial: true
}];
const TALK_CHAIN = [{
  stage: 'vad',
  kind: 'source',
  name: 'Mic',
  meta: '16 kHz'
}, {
  stage: 'vad',
  kind: 'vad',
  name: 'Silero',
  meta: 'gate'
}, {
  stage: 'stt',
  kind: 'stt',
  name: 'Whisper',
  meta: 'tiny.en'
}, {
  stage: 'agent',
  kind: 'agent',
  name: 'Agent',
  meta: '4o-mini'
}, {
  stage: 'tts',
  kind: 'tts',
  name: 'Piper',
  meta: 'amy-low'
}, {
  stage: 'out',
  kind: 'sink',
  name: 'Speaker',
  meta: 'live'
}];
function TalkView() {
  const [active, setActive] = React.useState(2);
  React.useEffect(() => {
    const t = setInterval(() => setActive(i => (i + 1) % TALK_CHAIN.length), 600);
    return () => clearInterval(t);
  }, []);
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "Talk",
    sub: "live conversation \xB7 watch it think"
  }, /*#__PURE__*/React.createElement(TkPill, {
    status: "live",
    label: "00:21"
  }), /*#__PURE__*/React.createElement(TkButton, {
    variant: "danger",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "phone-off",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "End session")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      minHeight: 0
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      display: 'flex',
      flexDirection: 'column',
      minWidth: 0
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: '20px 28px',
      display: 'flex',
      flexDirection: 'column'
    }
  }, TALK_LINES.map((l, i) => /*#__PURE__*/React.createElement(TkLine, _extends({
    key: i
  }, l)))), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 'none',
      padding: '14px 28px',
      borderTop: '1px solid var(--line-1)',
      background: 'var(--bg-panel)'
    }
  }, /*#__PURE__*/React.createElement(TkFlow, {
    nodes: TALK_CHAIN,
    running: true,
    activeIndex: active
  }))), /*#__PURE__*/React.createElement("aside", {
    style: {
      width: 280,
      flex: 'none',
      borderLeft: '1px solid var(--line-1)',
      background: 'var(--bg-panel)',
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      gap: 18,
      padding: '36px 24px'
    }
  }, /*#__PURE__*/React.createElement(TkOrb, {
    size: 132,
    live: true
  }), /*#__PURE__*/React.createElement(TkWave, {
    bars: 20,
    height: 32,
    live: true
  }), /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "agent speaking"), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 'auto',
      width: '100%',
      display: 'flex',
      flexDirection: 'column',
      gap: 12
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--text-3)'
    }
  }, "TTFB"), /*#__PURE__*/React.createElement("span", null, "118 ms")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--text-3)'
    }
  }, "turns"), /*#__PURE__*/React.createElement("span", null, "4")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      fontFamily: 'var(--font-mono)',
      fontSize: 12,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--text-3)'
    }
  }, "barge-in"), /*#__PURE__*/React.createElement("span", null, "1"))))));
}
window.TalkView = TalkView;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/TalkView.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/TtsPlayground.jsx
try { (() => {
// Voxa Studio — TTS Playground (VST-002 §7): the v1 Voice Lab matured into a lab —
// take history, waveform scrubber, A/B/X blind test, stress phrases, batch bench.
const {
  Button: TtButton,
  Badge: TtBadge,
  Card: TtCard,
  Input: TtInput
} = window.VOXADesignSystem_4f47fa;
const TT_VOICES = [{
  v: 'amy-low',
  eng: 'Piper',
  ttfb: 96,
  rtf: 0.21
}, {
  v: 'amy-medium',
  eng: 'Piper',
  ttfb: 128,
  rtf: 0.29
}, {
  v: 'ryan-high',
  eng: 'Piper',
  ttfb: 151,
  rtf: 0.34
}, {
  v: 'af_sky',
  eng: 'Kokoro',
  ttfb: 204,
  rtf: 0.41
}, {
  v: 'am_adam',
  eng: 'Kokoro',
  ttfb: 212,
  rtf: 0.44
}];
const TT_STRESS = ['$1,204.50 on 03/14', 'read HTTP/2 aloud', 'the wound was wound', 'naïve café résumé', 'SELECT * FROM users;'];
const TT_TAKES = [{
  t: 'amy-low',
  d: '2.4s',
  ttfb: 96
}, {
  t: 'amy-medium',
  d: '2.5s',
  ttfb: 128
}, {
  t: 'af_sky',
  d: '2.6s',
  ttfb: 204
}];
const TT_WAVE = Array.from({
  length: 56
}, (_, i) => 0.25 + 0.7 * Math.abs(Math.sin(i * 0.5) * Math.cos(i * 0.17)));
function Scrubber() {
  const [pos, setPos] = React.useState(0.42);
  return /*#__PURE__*/React.createElement("div", {
    onClick: e => {
      const r = e.currentTarget.getBoundingClientRect();
      setPos((e.clientX - r.left) / r.width);
    },
    style: {
      position: 'relative',
      display: 'flex',
      alignItems: 'flex-end',
      gap: 2,
      height: 56,
      cursor: 'pointer',
      padding: '0 2px'
    }
  }, TT_WAVE.map((h, i) => /*#__PURE__*/React.createElement("div", {
    key: i,
    style: {
      flex: 1,
      height: `${h * 100}%`,
      borderRadius: 999,
      background: i / TT_WAVE.length < pos ? 'var(--pulse-400)' : 'var(--ink-600)'
    }
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'absolute',
      top: -3,
      bottom: -3,
      left: `${pos * 100}%`,
      width: 2,
      background: 'var(--text-1)',
      boxShadow: '0 0 8px var(--pulse-400)'
    }
  }));
}
function TtsPlayground() {
  const [voice, setVoice] = React.useState('amy-low');
  const [text, setText] = React.useState('Your order shipped this morning and arrives Thursday.');
  const [revealed, setRevealed] = React.useState(false);
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(ViewBar, {
    title: "TTS Playground",
    sub: "real engines \xB7 TTFB / RTF \xB7 A/B/X blind \xB7 batch bench"
  }, /*#__PURE__*/React.createElement(TtButton, {
    variant: "secondary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "download",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Export WAV"), /*#__PURE__*/React.createElement(TtButton, {
    variant: "primary",
    size: "sm",
    icon: /*#__PURE__*/React.createElement("i", {
      "data-lucide": "audio-lines",
      style: {
        width: 14,
        height: 14
      }
    })
  }, "Synthesize")), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      overflowY: 'auto',
      padding: 20,
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(TtInput, {
    value: text,
    onChange: setText,
    style: {
      width: '100%'
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 8,
      flexWrap: 'wrap',
      alignItems: 'center'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      marginRight: 4
    }
  }, "stress phrases"), TT_STRESS.map(p => /*#__PURE__*/React.createElement("button", {
    key: p,
    onClick: () => setText(p),
    style: {
      padding: '5px 10px',
      borderRadius: 999,
      border: '1px solid var(--line-2)',
      background: 'var(--surface-2)',
      color: 'var(--text-2)',
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      cursor: 'pointer'
    }
  }, p))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      alignItems: 'flex-start',
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement(TtCard, {
    style: {
      width: 280,
      flex: 'none',
      padding: 16
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label"
  }, "Voice catalog"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 6,
      marginTop: 12
    }
  }, TT_VOICES.map(v => {
    const on = voice === v.v;
    return /*#__PURE__*/React.createElement("button", {
      key: v.v,
      onClick: () => setVoice(v.v),
      style: {
        display: 'grid',
        gridTemplateColumns: '1fr auto auto',
        alignItems: 'center',
        gap: 10,
        padding: '9px 11px',
        borderRadius: 'var(--r-md)',
        cursor: 'pointer',
        textAlign: 'left',
        border: '1px solid',
        borderColor: on ? 'var(--line-3)' : 'var(--line-1)',
        background: on ? 'var(--surface-3)' : 'var(--surface-2)'
      }
    }, /*#__PURE__*/React.createElement("span", {
      style: {
        minWidth: 0
      }
    }, /*#__PURE__*/React.createElement("span", {
      style: {
        fontSize: 13,
        color: 'var(--text-1)',
        fontWeight: on ? 600 : 400
      }
    }, v.v), /*#__PURE__*/React.createElement("span", {
      style: {
        display: 'block',
        fontFamily: 'var(--font-mono)',
        fontSize: 10,
        color: 'var(--text-muted)'
      }
    }, v.eng)), /*#__PURE__*/React.createElement("span", {
      style: {
        fontFamily: 'var(--font-mono)',
        fontSize: 11,
        color: 'var(--pulse-300)'
      }
    }, v.ttfb, "ms"), /*#__PURE__*/React.createElement("span", {
      style: {
        fontFamily: 'var(--font-mono)',
        fontSize: 11,
        color: 'var(--text-3)'
      }
    }, v.rtf));
  }))), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1,
      minWidth: 320,
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(TtCard, {
    style: {
      padding: 18
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between'
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Synthesis \xB7 ", voice), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, "scrub to play")), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 12
    }
  }, /*#__PURE__*/React.createElement(Scrubber, null)), /*#__PURE__*/React.createElement("div", {
    style: {
      borderTop: '1px solid var(--line-1)',
      marginTop: 14,
      paddingTop: 12,
      display: 'flex',
      flexDirection: 'column',
      gap: 8
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label"
  }, "take history"), TT_TAKES.map((tk, i) => /*#__PURE__*/React.createElement("div", {
    key: i,
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 12
    }
  }, /*#__PURE__*/React.createElement("button", {
    style: {
      width: 26,
      height: 26,
      borderRadius: '50%',
      flex: 'none',
      border: '1px solid var(--line-2)',
      background: 'var(--surface-3)',
      color: 'var(--pulse-400)',
      cursor: 'pointer',
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center'
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": "play",
    style: {
      width: 12,
      height: 12
    }
  })), /*#__PURE__*/React.createElement("span", {
    style: {
      flex: 1,
      fontSize: 13,
      color: 'var(--text-2)'
    }
  }, tk.t), /*#__PURE__*/React.createElement("span", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 11,
      color: 'var(--text-3)'
    }
  }, tk.d, " \xB7 ", tk.ttfb, "ms"))))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement(TtCard, {
    style: {
      flex: 1,
      minWidth: 240,
      padding: 18
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "A / B / X blind test"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 8,
      marginTop: 14
    }
  }, ['A', 'B', 'X'].map(k => /*#__PURE__*/React.createElement("button", {
    key: k,
    style: {
      flex: 1,
      padding: '10px 0',
      borderRadius: 'var(--r-md)',
      border: '1px solid var(--line-2)',
      background: k === 'X' ? 'var(--accent-soft)' : 'var(--surface-3)',
      color: k === 'X' ? 'var(--pulse-300)' : 'var(--text-1)',
      fontWeight: 600,
      cursor: 'pointer'
    }
  }, k))), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 12,
      fontSize: 12.5,
      color: 'var(--text-3)'
    }
  }, revealed ? /*#__PURE__*/React.createElement("span", null, "X was ", /*#__PURE__*/React.createElement("b", {
    style: {
      color: 'var(--text-1)'
    }
  }, "B \xB7 af_sky"), ". You picked B.") : 'X is randomly A or B. Vote, then reveal.'), /*#__PURE__*/React.createElement(TtButton, {
    variant: "secondary",
    size: "sm",
    onClick: () => setRevealed(x => !x),
    style: {
      marginTop: 12
    }
  }, revealed ? 'Reset' : 'Reveal')), /*#__PURE__*/React.createElement(TtCard, {
    style: {
      flex: 1,
      minWidth: 240,
      padding: 18
    }
  }, /*#__PURE__*/React.createElement("div", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "Batch bench \xB7 phrase deck"), /*#__PURE__*/React.createElement("table", {
    style: {
      width: '100%',
      borderCollapse: 'collapse',
      marginTop: 12,
      fontSize: 12
    }
  }, /*#__PURE__*/React.createElement("thead", null, /*#__PURE__*/React.createElement("tr", {
    style: {
      color: 'var(--text-muted)',
      fontFamily: 'var(--font-mono)',
      fontSize: 10,
      textTransform: 'uppercase',
      letterSpacing: '0.06em'
    }
  }, /*#__PURE__*/React.createElement("td", {
    style: {
      padding: '0 0 8px'
    }
  }, "voice"), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right'
    }
  }, "p50"), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right'
    }
  }, "p95"), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right'
    }
  }, "rtf"))), /*#__PURE__*/React.createElement("tbody", {
    style: {
      fontFamily: 'var(--font-mono)'
    }
  }, TT_VOICES.slice(0, 4).map(v => /*#__PURE__*/React.createElement("tr", {
    key: v.v,
    style: {
      borderTop: '1px solid var(--line-1)',
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("td", {
    style: {
      padding: '7px 0',
      fontFamily: 'var(--font-ui)'
    }
  }, v.v), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right',
      color: 'var(--pulse-300)'
    }
  }, v.ttfb), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right'
    }
  }, Math.round(v.ttfb * 1.4)), /*#__PURE__*/React.createElement("td", {
    style: {
      textAlign: 'right'
    }
  }, v.rtf)))))))))));
}
window.TtsPlayground = TtsPlayground;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/TtsPlayground.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/app.jsx
try { (() => {
// Voxa Studio — app shell: splash → desktop shell, view routing, session clock.
function fmt(s) {
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
}
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
    const t = setInterval(() => setClock(c => c + 1), 1000);
    return () => clearInterval(t);
  }, [live]);

  // refresh icons after every render (views inject new <i data-lucide>)
  React.useEffect(() => {
    window.renderStudioIcons && window.renderStudioIcons();
  });
  const view = {
    talk: /*#__PURE__*/React.createElement(window.TalkView, null),
    playgrounds: /*#__PURE__*/React.createElement(window.PlaygroundsView, null),
    builder: /*#__PURE__*/React.createElement(window.PipelineBuilder, null),
    metrics: /*#__PURE__*/React.createElement(window.MetricsWorkbench, null),
    models: /*#__PURE__*/React.createElement(window.ModelsView, null),
    config: /*#__PURE__*/React.createElement(window.ConfigView, {
      onOpenBuilder: () => setNav('builder')
    })
  }[nav];
  return /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(StudioShell, {
    nav: nav,
    onNav: setNav,
    live: live,
    sessionTime: fmt(clock),
    onReplaySplash: () => setSplash(true)
  }, view), splash && /*#__PURE__*/React.createElement("div", {
    onClick: () => setSplash(false),
    style: {
      position: 'fixed',
      inset: 0,
      zIndex: 50,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      background: 'rgba(5, 7, 10, 0.82)',
      backdropFilter: 'blur(6px)'
    }
  }, /*#__PURE__*/React.createElement(Splash, {
    onSkip: () => setSplash(false)
  })));
}
ReactDOM.createRoot(document.getElementById('root')).render(/*#__PURE__*/React.createElement(StudioApp, null));
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/app.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/canvasNodes.jsx
try { (() => {
// Voxa Studio — Pipeline Builder canvas data + renderers (VST-002 §8).
// Typed ports carry a frame type; colors are the §3.3 stage palette.
const FRAME_COLORS = {
  audio: 'var(--stage-vad)',
  // grey — raw/audio frames
  transcription: 'var(--stage-stt)',
  // cyan — text from STT
  agentText: 'var(--stage-agent)',
  // violet — agent output text
  synthAudio: 'var(--stage-tts)' // amber — synthesized audio
};
const FRAME_LABEL = {
  audio: 'audio',
  transcription: 'transcription',
  agentText: 'agent-text',
  synthAudio: 'synth-audio'
};

// The default chain the brief draws. Single-in / single-out (the honesty constraint, §8.3).
const CANVAS_W = 1000,
  CANVAS_H = 300;
const STUDIO_NODES = [{
  id: 'mic',
  stage: 'vad',
  kind: 'source',
  name: 'Mic',
  meta: '16 kHz',
  x: 20,
  y: 118,
  w: 124,
  h: 60,
  inType: null,
  outType: 'audio'
}, {
  id: 'vad',
  stage: 'vad',
  kind: 'vad',
  name: 'Silero VAD',
  meta: 'stop 800 ms',
  x: 188,
  y: 118,
  w: 132,
  h: 60,
  inType: 'audio',
  outType: 'audio'
}, {
  id: 'stt',
  stage: 'stt',
  kind: 'stt',
  name: 'WhisperCpp',
  meta: 'tiny.en · cached',
  x: 364,
  y: 118,
  w: 132,
  h: 60,
  inType: 'audio',
  outType: 'transcription',
  cached: true
}, {
  id: 'agent',
  stage: 'agent',
  kind: 'agent',
  name: 'Agent',
  meta: 'OpenAI · 4o-mini',
  x: 540,
  y: 118,
  w: 132,
  h: 60,
  inType: 'transcription',
  outType: 'agentText'
}, {
  id: 'tts',
  stage: 'tts',
  kind: 'tts',
  name: 'Piper TTS',
  meta: 'amy-low · 16 kHz',
  x: 716,
  y: 118,
  w: 132,
  h: 60,
  inType: 'agentText',
  outType: 'synthAudio',
  cached: true
}, {
  id: 'speaker',
  stage: 'out',
  kind: 'sink',
  name: 'Speaker',
  meta: 'default device',
  x: 892,
  y: 118,
  w: 108,
  h: 60,
  inType: 'synthAudio',
  outType: null
}];
const STUDIO_EDGES = [['mic', 'vad'], ['vad', 'stt'], ['stt', 'agent'], ['agent', 'tts'], ['tts', 'speaker']];
const PALETTE = [{
  kind: 'source',
  label: 'Source',
  type: 'audio'
}, {
  kind: 'vad',
  label: 'VAD',
  type: 'audio'
}, {
  kind: 'stt',
  label: 'STT provider',
  type: 'transcription'
}, {
  kind: 'filter',
  label: 'TranscriptionFilter',
  type: 'transcription'
}, {
  kind: 'agent',
  label: 'Agent',
  type: 'agentText'
}, {
  kind: 'aggregator',
  label: 'SentenceAggregator',
  type: 'agentText'
}, {
  kind: 'tts',
  label: 'TTS provider',
  type: 'synthAudio'
}, {
  kind: 'sink',
  label: 'Sink',
  type: 'synthAudio'
}, {
  kind: 'tap',
  label: 'DiagnosticsTap',
  type: 'audio'
}];
function nodeById(id) {
  return STUDIO_NODES.find(n => n.id === id);
}
function outPort(n) {
  return {
    x: n.x + n.w,
    y: n.y + n.h / 2
  };
}
function inPort(n) {
  return {
    x: n.x,
    y: n.y + n.h / 2
  };
}
function CanvasEdges({
  active
}) {
  return /*#__PURE__*/React.createElement("svg", {
    width: CANVAS_W,
    height: CANVAS_H,
    style: {
      position: 'absolute',
      inset: 0,
      pointerEvents: 'none',
      overflow: 'visible'
    }
  }, /*#__PURE__*/React.createElement("defs", null, /*#__PURE__*/React.createElement("marker", {
    id: "vx-arr",
    viewBox: "0 0 10 10",
    refX: "8.5",
    refY: "5",
    markerWidth: "7",
    markerHeight: "7",
    orient: "auto"
  }, /*#__PURE__*/React.createElement("path", {
    d: "M0,0 L10,5 L0,10 z",
    fill: "var(--pulse-400)"
  }))), STUDIO_EDGES.map(([a, b]) => {
    const p1 = outPort(nodeById(a)),
      p2 = inPort(nodeById(b));
    const mx = (p1.x + p2.x) / 2;
    const d = `M${p1.x},${p1.y} C${mx},${p1.y} ${mx},${p2.y} ${p2.x - 7},${p2.y}`;
    return /*#__PURE__*/React.createElement("g", {
      key: a + b
    }, /*#__PURE__*/React.createElement("path", {
      d: d,
      fill: "none",
      stroke: "var(--pulse-400)",
      strokeWidth: "2",
      markerEnd: "url(#vx-arr)",
      opacity: active ? 0.9 : 0.55
    }), active && /*#__PURE__*/React.createElement("circle", {
      r: "3.2",
      fill: "var(--pulse-300)"
    }, /*#__PURE__*/React.createElement("animateMotion", {
      dur: "1.1s",
      repeatCount: "indefinite",
      path: d
    })));
  }));
}
function Port({
  type,
  side
}) {
  return /*#__PURE__*/React.createElement("span", {
    style: {
      position: 'absolute',
      top: '50%',
      [side]: -6,
      transform: 'translateY(-50%)',
      width: 11,
      height: 11,
      borderRadius: '50%',
      background: FRAME_COLORS[type] || 'var(--ink-400)',
      border: '2px solid var(--bg-page)',
      boxShadow: '0 0 0 1px var(--line-2)'
    },
    title: FRAME_LABEL[type]
  });
}
function CanvasNode({
  node,
  selected,
  active,
  onSelect
}) {
  const accent = `var(--stage-${node.stage})`;
  return /*#__PURE__*/React.createElement("div", {
    onClick: () => onSelect(node.id),
    style: {
      position: 'absolute',
      left: node.x,
      top: node.y,
      width: node.w,
      height: node.h,
      background: 'var(--surface-2)',
      borderRadius: 'var(--r-md)',
      cursor: 'pointer',
      border: '1px solid',
      borderColor: selected ? accent : 'var(--line-2)',
      borderLeft: `3px solid ${accent}`,
      boxShadow: active ? `0 0 0 1px ${accent}, 0 0 22px -4px ${accent}` : selected ? `0 0 0 1px ${accent}` : 'var(--shadow-1)',
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      gap: 3,
      padding: '0 14px',
      transition: 'box-shadow var(--dur-standard) var(--ease-out), border-color var(--dur-fast) var(--ease-out)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 9.5,
      letterSpacing: '0.08em',
      textTransform: 'uppercase',
      color: accent
    }
  }, node.kind), /*#__PURE__*/React.createElement("div", {
    style: {
      fontSize: 13,
      fontWeight: 600,
      color: 'var(--text-1)'
    }
  }, node.name), /*#__PURE__*/React.createElement("div", {
    style: {
      fontFamily: 'var(--font-mono)',
      fontSize: 10,
      color: 'var(--text-3)',
      display: 'flex',
      alignItems: 'center',
      gap: 5
    }
  }, node.meta, node.cached && /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--ok)'
    }
  }, "\u2713")), node.inType && /*#__PURE__*/React.createElement(Port, {
    type: node.inType,
    side: "left"
  }), node.outType && /*#__PURE__*/React.createElement(Port, {
    type: node.outType,
    side: "right"
  }));
}
Object.assign(window, {
  FRAME_COLORS,
  FRAME_LABEL,
  CANVAS_W,
  CANVAS_H,
  STUDIO_NODES,
  STUDIO_EDGES,
  PALETTE,
  CanvasEdges,
  CanvasNode
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/canvasNodes.jsx", error: String((e && e.message) || e) }); }

// ui_kits/studio/icons.js
try { (() => {
// Voxa Studio — React-safe icon rendering. Instead of lucide.createIcons (which
// REPLACES the <i> node and breaks React reconciliation), we keep each
// React-owned <i data-lucide> in place and inject the SVG as its innerHTML.
// React sees the <i> as a childless leaf, so it never reconciles the SVG.
(function () {
  function pascal(name) {
    return name.split('-').map(s => s.charAt(0).toUpperCase() + s.slice(1)).join('');
  }
  window.renderStudioIcons = function (root) {
    var L = window.lucide;
    if (!L || !L.icons) return;
    (root || document).querySelectorAll('i[data-lucide]').forEach(function (el) {
      var name = el.getAttribute('data-lucide');
      if (el.__iconName === name) return; // already drawn this glyph
      var node = L.icons[pascal(name)] || L.icons[name];
      if (!node) return;
      var w = parseInt(el.style.width, 10) || 16;
      var h = parseInt(el.style.height, 10) || 16;
      var inner = node.map(function (child) {
        var tag = child[0],
          attrs = child[1] || {};
        var a = Object.keys(attrs).map(function (k) {
          return k + '="' + attrs[k] + '"';
        }).join(' ');
        return '<' + tag + ' ' + a + '></' + tag + '>';
      }).join('');
      el.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" ' + 'stroke-linecap="round" stroke-linejoin="round" style="display:block">' + inner + '</svg>';
      if (!el.style.display) el.style.display = 'inline-flex';
      el.__iconName = name;
    });
  };
})();
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/studio/icons.js", error: String((e && e.message) || e) }); }

// ui_kits/website/Features.jsx
try { (() => {
const {
  Card,
  PipelineFlow
} = window.VOXADesignSystem_4f47fa;
const FEATURES = [{
  icon: 'workflow',
  title: 'Frames all the way down',
  body: 'Audio, transcription, LLM tokens and control signals are all frames in one ordered stream — interruptions and barge-in fall out of the model for free.'
}, {
  icon: 'bot',
  title: 'Agent Framework native',
  body: 'Drop a Microsoft Agent Framework agent into the pipeline as a processor. Tools, memory and handoffs work mid-conversation.'
}, {
  icon: 'cloud',
  title: 'Azure end to end',
  body: 'First-class processors for Azure Speech, Azure OpenAI and ACS telephony. Deploy as a container app; scale per session.'
}];
function Features() {
  return /*#__PURE__*/React.createElement("section", {
    style: {
      padding: '0 40px 80px'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      maxWidth: 'var(--container-max)',
      margin: '0 auto'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      border: '1px solid var(--line-1)',
      borderRadius: 'var(--r-xl)',
      background: 'var(--bg-panel)',
      padding: '28px 28px 24px',
      overflowX: 'auto'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      display: 'block',
      marginBottom: 16
    }
  }, "One pipeline \xB7 four stages \xB7 <500 ms round trip"), /*#__PURE__*/React.createElement(PipelineFlow, {
    running: true,
    nodes: [{
      stage: 'audio',
      name: 'WebRtcTransport',
      meta: '48 kHz · opus'
    }, {
      stage: 'stt',
      name: 'AzureSpeechStt',
      meta: 'streaming'
    }, {
      stage: 'llm',
      name: 'AgentTurn',
      meta: 'Agent Framework'
    }, {
      stage: 'tts',
      name: 'AzureNeuralTts',
      meta: 'neural voices'
    }]
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(3, 1fr)',
      gap: 18,
      marginTop: 18
    }
  }, FEATURES.map(f => /*#__PURE__*/React.createElement(Card, {
    key: f.title,
    style: {
      padding: 24
    }
  }, /*#__PURE__*/React.createElement("i", {
    "data-lucide": f.icon,
    style: {
      width: 22,
      height: 22,
      color: 'var(--pulse-400)'
    }
  }), /*#__PURE__*/React.createElement("h3", {
    style: {
      fontSize: 17,
      fontWeight: 600,
      marginTop: 14
    }
  }, f.title), /*#__PURE__*/React.createElement("p", {
    style: {
      fontSize: 13,
      color: 'var(--text-2)',
      marginTop: 8
    }
  }, f.body))))));
}
window.Features = Features;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/website/Features.jsx", error: String((e && e.message) || e) }); }

// ui_kits/website/Footer.jsx
try { (() => {
function Footer() {
  return /*#__PURE__*/React.createElement("footer", {
    style: {
      borderTop: '1px solid var(--line-1)',
      padding: '28px 40px'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      maxWidth: 'var(--container-max)',
      margin: '0 auto',
      display: 'flex',
      alignItems: 'center',
      gap: 24
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: (window.DS_BASE || '../..') + '/assets/voxa-wordmark-mono.svg',
    alt: "VOXA",
    style: {
      height: 16,
      opacity: 0.7
    }
  }), /*#__PURE__*/React.createElement("span", {
    style: {
      fontSize: 12,
      color: 'var(--text-muted)'
    }
  }, "\xA9 2026 Voxa Labs"), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 20
    }
  }, /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "Docs"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "NuGet"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "GitHub"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 12,
      color: 'var(--text-3)'
    }
  }, "Privacy"))));
}
window.Footer = Footer;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/website/Footer.jsx", error: String((e && e.message) || e) }); }

// ui_kits/website/Hero.jsx
try { (() => {
const {
  Button,
  Badge,
  CodeBlock,
  VoiceOrb,
  Waveform
} = window.VOXADesignSystem_4f47fa;
const SAMPLE = `var pipeline = new VoxaPipeline()
    .Use<WebRtcTransport>()
    .Use<AzureSpeechStt>()
    .Use<AgentTurn>("support-agent")   // Microsoft Agent Framework
    .Use<AzureNeuralTts>("en-US-Ava");

await pipeline.RunAsync();`;
function Hero() {
  return /*#__PURE__*/React.createElement("section", {
    style: {
      position: 'relative',
      padding: '88px 40px 72px',
      overflow: 'hidden'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'absolute',
      inset: 0,
      pointerEvents: 'none',
      background: 'radial-gradient(700px 380px at 72% 30%, rgba(79,195,247,0.10), transparent 70%)'
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'relative',
      maxWidth: 'var(--container-max)',
      margin: '0 auto',
      display: 'grid',
      gridTemplateColumns: '1.05fr 1fr',
      gap: 56,
      alignItems: 'center'
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("span", {
    className: "vx-label",
    style: {
      color: 'var(--pulse-300)'
    }
  }, "frame-based \xB7 real-time \xB7 .NET"), /*#__PURE__*/React.createElement("h1", {
    style: {
      fontSize: 'var(--fs-5xl)',
      marginTop: 14
    }
  }, "Voice agents,", /*#__PURE__*/React.createElement("br", null), "frame by frame."), /*#__PURE__*/React.createElement("p", {
    style: {
      fontSize: 'var(--fs-lg)',
      color: 'var(--text-2)',
      marginTop: 18,
      maxWidth: '46ch'
    }
  }, "VOXA is a real-time voice pipeline framework for .NET. Compose speech, reasoning and synthesis as frame processors \u2014 built on Microsoft Agent Framework and Azure."), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 14,
      marginTop: 28
    }
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "primary",
    size: "lg"
  }, "Get started"), /*#__PURE__*/React.createElement("code", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 10,
      height: 46,
      padding: '0 18px',
      background: 'var(--surface-2)',
      border: '1px solid var(--line-2)',
      borderRadius: 'var(--r-md)',
      fontSize: 13,
      color: 'var(--text-2)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--pulse-400)'
    }
  }, "$"), " dotnet add package Voxa"))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 18,
      alignItems: 'center'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 22
    }
  }, /*#__PURE__*/React.createElement(VoiceOrb, {
    size: 104,
    live: true
  }), /*#__PURE__*/React.createElement(Waveform, {
    live: true,
    gradient: true,
    bars: 18,
    height: 40
  })), /*#__PURE__*/React.createElement(CodeBlock, {
    file: "Program.cs",
    badge: "C#",
    code: SAMPLE,
    style: {
      width: '100%'
    }
  }))));
}
window.Hero = Hero;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/website/Hero.jsx", error: String((e && e.message) || e) }); }

// ui_kits/website/SiteNav.jsx
try { (() => {
const {
  Button,
  Badge
} = window.VOXADesignSystem_4f47fa;
function SiteNav() {
  return /*#__PURE__*/React.createElement("nav", {
    style: {
      position: 'sticky',
      top: 0,
      zIndex: 10,
      display: 'flex',
      alignItems: 'center',
      gap: 28,
      padding: '14px 40px',
      borderBottom: '1px solid var(--line-1)',
      background: 'var(--glass-bg)',
      backdropFilter: 'var(--blur-glass)'
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: (window.DS_BASE || '../..') + '/assets/voxa-logo.svg',
    alt: "VOXA",
    style: {
      height: 28
    }
  }), /*#__PURE__*/React.createElement(Badge, {
    tone: "pulse"
  }, "v0.4 preview"), /*#__PURE__*/React.createElement("div", {
    style: {
      flex: 1
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 22
    }
  }, /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 13,
      fontWeight: 500,
      color: 'var(--text-2)'
    }
  }, "Docs"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 13,
      fontWeight: 500,
      color: 'var(--text-2)'
    }
  }, "Examples"), /*#__PURE__*/React.createElement("a", {
    href: "#",
    style: {
      fontSize: 13,
      fontWeight: 500,
      color: 'var(--text-2)'
    }
  }, "GitHub"), /*#__PURE__*/React.createElement(Button, {
    variant: "primary",
    size: "sm"
  }, "Get started")));
}
window.SiteNav = SiteNav;
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/website/SiteNav.jsx", error: String((e && e.message) || e) }); }

// ui_kits/website/site.jsx
try { (() => {
function Site() {
  React.useEffect(() => {
    window.lucide && lucide.createIcons({
      attrs: {
        'stroke-width': 1.75
      }
    });
  });
  return /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement(SiteNav, null), /*#__PURE__*/React.createElement(Hero, null), /*#__PURE__*/React.createElement(Features, null), /*#__PURE__*/React.createElement(Footer, null));
}
ReactDOM.createRoot(document.getElementById('root')).render(/*#__PURE__*/React.createElement(Site, null));
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/website/site.jsx", error: String((e && e.message) || e) }); }

__ds_ns.Badge = __ds_scope.Badge;

__ds_ns.Button = __ds_scope.Button;

__ds_ns.Card = __ds_scope.Card;

__ds_ns.CodeBlock = __ds_scope.CodeBlock;

__ds_ns.IconButton = __ds_scope.IconButton;

__ds_ns.Tabs = __ds_scope.Tabs;

__ds_ns.Input = __ds_scope.Input;

__ds_ns.Select = __ds_scope.Select;

__ds_ns.Switch = __ds_scope.Switch;

__ds_ns.MetricStat = __ds_scope.MetricStat;

__ds_ns.PipelineFlow = __ds_scope.PipelineFlow;

__ds_ns.PipelineNode = __ds_scope.PipelineNode;

__ds_ns.StatusPill = __ds_scope.StatusPill;

__ds_ns.TranscriptLine = __ds_scope.TranscriptLine;

__ds_ns.VoiceOrb = __ds_scope.VoiceOrb;

__ds_ns.Waveform = __ds_scope.Waveform;

})();
