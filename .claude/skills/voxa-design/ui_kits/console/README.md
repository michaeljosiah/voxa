# VOXA Console — UI kit

The operator dashboard for VOXA deployments: monitor live sessions, inspect frame pipelines, manage voices and keys.

This is a **fictional reference surface** (VOXA is a new brand with no shipped product yet) — it defines what the Console should look like, built entirely from the design-system primitives.

- `index.html` — interactive demo: sessions overview → click a row → live session detail with running pipeline + streaming transcript.
- `ConsoleShell.jsx` — sidebar + topbar chrome.
- `SessionsView.jsx` — metrics row + sessions table.
- `LiveSessionView.jsx` — pipeline flow, transcript tabs, session controls.
- `app.jsx` — wiring + fake data.

Layout rules: sidebar fixed 232px on `--bg-panel`; content on `--bg-page` with 24px gutters; one primary button per view.
