namespace Housekeeper.Api;

/// <summary>
/// The stylesheet both pages share. One place, so the dashboard and the settings screen cannot drift apart.
///
/// Every colour is a variable with a light and a dark value, and every form control sets its own background
/// and text colour. That matters: declaring <c>color-scheme: light dark</c> without styling the controls
/// leaves the browser painting inputs from the dark palette on top of a light page.
///
/// The look is quiet but not colourless: slate neutrals that carry a little blue, hairline borders, one warm
/// accent for the mark and the primary button, one cool tint for emphasis, and a hue per kind of finding
/// that appears only as a heading's dot and a card's edge. Colour marks state — an error, a warning,
/// something live, something asked for — so that when it appears it means something.
/// </summary>
internal static class Ui
{
    public const string Styles = """
  *, *::before, *::after { box-sizing: border-box; }
  [hidden] { display: none !important; }

  :root {
    color-scheme: light dark;

    /* Slate, not grey: every neutral carries a little blue so the page reads as designed rather than unset. */
    --bg: #f3f5f9;
    --panel: #ffffff;
    --panel-2: #f6f8fb;
    --ink: #17202b;
    --ink-soft: #4b5563;
    --muted: #7b8494;
    --line: #dfe4ec;
    --line-soft: #ebeff4;

    /* One accent for the mark, the primary button and the live count; one tint for calm emphasis. */
    --accent: #e0632a;
    --accent-2: #f27a42;
    --accent-soft: #fdeee6;
    --tint: #2f5bd7;
    --tint-soft: #e9eefc;
    --link: #2f5bd7;
    --focus: #2f5bd7;

    --field: #ffffff;
    --field-line: #cfd6e1;

    --ok-text: #16803c; --ok-bg: #e8f7ee; --ok-line: #b9e6c9;
    --warn-text: #935108; --warn-bg: #fff6e5; --warn-line: #f8dfa6;
    --err-text: #c02626; --err-bg: #fdecec; --err-line: #f5bcbc;
    --live-bg: #e9eefc; --live-text: #2f4ec2;

    /* A hue per kind of finding, used sparingly: the heading's dot and the card's edge. */
    --kind-stuck: #d97706;
    --kind-outlier: #7c3aed;
    --kind-quiet: #0f8b8d;
    --kind-missing: #c02626;
    --kind-concern: #e0632a;
    --kind-habit: #2f855a;

    --meter-fill: #2f5bd7;
    --meter-track: #dfe4ec;

    --radius: 10px;
    --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  }

  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0f1218;
      --panel: #171b23;
      --panel-2: #1d222c;
      --ink: #eef1f6;
      --ink-soft: #b3bbc9;
      --muted: #7d8696;
      --line: #262c38;
      --line-soft: #202632;

      --accent: #f27a42;
      --accent-2: #f8935f;
      --accent-soft: #3a2418;
      --tint: #7c9cf5;
      --tint-soft: #1b2440;
      --link: #9db4f8;
      --focus: #7c9cf5;

      --field: #12161d;
      --field-line: #333b49;

      --ok-text: #4ade80; --ok-bg: #0f2a1b; --ok-line: #1f5c36;
      --warn-text: #fbbf24; --warn-bg: #2a2008; --warn-line: #6b4d0a;
      --err-text: #f87171; --err-bg: #2f1414; --err-line: #7a2424;
      --live-bg: #1b2440; --live-text: #a9bdfb;

      --kind-stuck: #fbbf24;
      --kind-outlier: #c4b5fd;
      --kind-quiet: #5eead4;
      --kind-missing: #f87171;
      --kind-concern: #f8935f;
      --kind-habit: #6ee7b7;

      --meter-fill: #7c9cf5;
      --meter-track: #262c38;
    }
  }

  html { scroll-behavior: smooth; }

  body {
    margin: 0;
    background: var(--bg);
    color: var(--ink);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, Roboto, "Helvetica Neue", sans-serif;
    font-size: 14px;
    line-height: 1.55;
    -webkit-font-smoothing: antialiased;
  }

  /* ---- header ---- */

  header.top {
    position: sticky; top: 0; z-index: 20;
    display: flex; align-items: center; gap: 20px;
    padding: 0 24px; height: 52px;
    background: var(--panel);
    border-bottom: 1px solid var(--line);
  }
  header.top .brand {
    display: inline-flex; align-items: center; gap: 8px;
    font-size: 14px; font-weight: 600; letter-spacing: -.01em; color: var(--ink); text-decoration: none;
  }
  header.top .brand .mark {
    width: 22px; height: 22px; border-radius: 6px; background: linear-gradient(160deg, var(--accent-2), var(--accent));
    display: grid; place-items: center; color: #fff;
  }
  header.top .brand .mark svg { width: 12px; height: 12px; }
  header.top nav { display: flex; gap: 4px; }
  header.top nav a {
    color: var(--muted); text-decoration: none; font-size: 13.5px; font-weight: 500;
    padding: 5px 9px; border-radius: 6px;
  }
  header.top nav a:hover { color: var(--ink); background: var(--panel-2); }
  header.top nav a.here { color: var(--tint); background: var(--tint-soft); }
  header.top nav a { display: inline-flex; align-items: center; gap: 6px; }
  header.top .badge {
    display: inline-grid; place-items: center; min-width: 18px; height: 18px; padding: 0 5px;
    border-radius: 999px; font-size: 11px; font-weight: 600; line-height: 1;
    background: var(--tint-soft); color: var(--tint);
  }
  header.top .badge.serious { background: var(--err-text); color: #fff; }
  header.top .spacer { margin-left: auto; }

  /* The API token is only needed off loopback, so it lives behind a small button rather than in a
     permanent box that most installs never use. */
  header.top .token-wrap { display: flex; align-items: center; gap: 8px; }
  header.top button.key { padding: 5px 10px; font-size: 12.5px; color: var(--muted); }
  header.top button.key svg { width: 13px; height: 13px; }
  header.top button.key.set { color: var(--ok-text); }
  header.top button.key.needed { color: var(--err-text); border-color: var(--err-line); }
  header.top input.token { width: 260px; padding: 5px 9px; font-size: 12.5px; }

  /* ---- layout ---- */

  main { max-width: 860px; margin: 0 auto; padding: 36px 24px 96px; }
  h1.title { font-size: 20px; margin: 0 0 6px; letter-spacing: -.02em; font-weight: 600; }
  h2.section {
    font-size: 13px; font-weight: 600; color: var(--ink);
    margin: 44px 0 12px; padding-bottom: 10px; border-bottom: 1px solid var(--line);
    display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
  }
  h2.section .count { color: var(--muted); font-weight: 500; }
  h2.section .count.live { color: var(--accent); font-weight: 600; }
  h2.section .tools { margin-left: auto; display: flex; align-items: center; gap: 8px; }
  h3.kind {
    font-size: 12px; font-weight: 600; color: var(--ink-soft); margin: 22px 0 8px;
    display: flex; align-items: center; gap: 8px;
  }
  h3.kind::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
  h3.kind.kind-StuckState::before { background: var(--kind-stuck); }
  h3.kind.kind-NumericOutlier::before { background: var(--kind-outlier); }
  h3.kind.kind-Unavailable::before { background: var(--kind-quiet); }
  h3.kind.kind-MissingEntity::before { background: var(--kind-missing); }
  h3.kind.kind-Concern::before { background: var(--kind-concern); }
  h3.kind.kind-Habit::before { background: var(--kind-habit); }
  h3.kind:first-child { margin-top: 4px; }
  h3.kind .count { color: var(--muted); }
  p.lead { margin: 0 0 16px; }

  /* ---- controls ---- */

  input, textarea, select, button { font: inherit; }
  input[type=text], input[type=password], input[type=number], input[type=search], textarea, select {
    width: 100%; padding: 8px 10px; border-radius: 6px;
    background: var(--field); color: var(--ink);
    border: 1px solid var(--field-line);
  }
  input:focus-visible, textarea:focus-visible, select:focus-visible {
    outline: none; border-color: var(--focus);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--focus) 18%, transparent);
  }
  textarea { resize: vertical; line-height: 1.55; }
  input[type=checkbox] { width: 16px; height: 16px; accent-color: var(--ink); margin: 3px 0 0; }
  :focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

  button {
    cursor: pointer; padding: 7px 12px; border-radius: 6px; font-weight: 500; font-size: 13.5px;
    background: var(--panel); color: var(--ink); border: 1px solid var(--field-line);
    display: inline-flex; align-items: center; gap: 6px; justify-content: center;
    transition: background .1s ease, border-color .1s ease;
  }
  button svg { width: 14px; height: 14px; flex: none; }
  button:hover:not(:disabled) { background: var(--panel-2); }
  button:disabled { opacity: .45; cursor: default; }
  button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.primary:hover:not(:disabled) { background: var(--accent-2); border-color: var(--accent-2); }
  button.quiet { background: transparent; border-color: transparent; color: var(--ink-soft); }
  button.quiet:hover:not(:disabled) { background: var(--panel-2); color: var(--ink); }
  button.danger:hover:not(:disabled) { color: var(--err-text); }
  button.link {
    background: none; border: none; padding: 0; font-size: 13px;
    color: var(--link); text-decoration: underline; font-weight: 500;
  }
  button.link:hover:not(:disabled) { background: none; }
  button.small { padding: 4px 9px; font-size: 12.5px; }

  .segmented { display: inline-flex; gap: 2px; }
  .segmented button {
    border: none; background: transparent; padding: 3px 8px; font-size: 12.5px; font-weight: 500;
    color: var(--muted); border-radius: 5px;
  }
  .segmented button:hover:not(:disabled) { color: var(--ink); background: transparent; }
  .segmented button.on { color: var(--ink); background: var(--panel-2); }

  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .row.actions { margin-top: 14px; }

  /* ---- cards ---- */

  .card {
    background: var(--panel); border: 1px solid var(--line); border-radius: var(--radius);
    padding: 18px 20px; margin-bottom: 10px;
    box-shadow: 0 1px 2px rgba(23, 32, 43, .04);
  }
  .card-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; margin-bottom: 4px; }
  .card-head h3 { margin: 0; font-size: 15px; letter-spacing: -.01em; font-weight: 600; }
  .card-head .when { margin-left: auto; color: var(--muted); font-size: 12px; white-space: nowrap; }
  .meta { color: var(--ink-soft); font-size: 13.5px; }
  .meta + .meta { margin-top: 3px; }
  .quote { color: var(--muted); font-size: 13px; }

  .empty {
    color: var(--muted); font-size: 13.5px; text-align: center;
    padding: 36px 20px; border: 1px dashed var(--line); border-radius: var(--radius); background: var(--panel-2);
  }
  .empty strong { display: block; color: var(--ink-soft); font-weight: 500; margin-bottom: 2px; }

  pre {
    background: var(--panel-2); border: 1px solid var(--line-soft); border-radius: 6px;
    padding: 12px 14px; margin: 10px 0 0; overflow-x: auto;
    font-family: var(--mono); font-size: 12px; line-height: 1.55; color: var(--ink);
  }

  /* A block of text with a copy button in its corner. The button sits over the block's own padding, and
     the block leaves room on the right so the first line never runs underneath it. */
  .code { position: relative; margin-top: 10px; }
  .code pre { padding-right: 84px; margin-top: 0; }
  .code button.copy {
    position: absolute; top: 8px; right: 8px;
    display: inline-flex; align-items: center; gap: 5px;
    padding: 3px 8px; font-size: 12px; font-weight: 400; border-radius: 5px;
    color: var(--muted); background: var(--panel); border: 1px solid var(--line-soft);
  }
  .code button.copy:hover:not(:disabled) { color: var(--ink); border-color: var(--line); }
  .code button.copy svg { width: 13px; height: 13px; }
  .code button.copy.done { color: var(--ok-text); }

  /* ---- the story: what the automation does, in three rows ---- */

  .story { display: grid; grid-template-columns: 64px 1fr; gap: 8px 16px; align-items: start; margin: 14px 0 4px; }
  .story .lbl { font-size: 11px; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; color: var(--tint); padding-top: 3px; }
  .story ul { margin: 0; padding: 0; list-style: none; }
  .story li { font-size: 14px; }
  .story li + li { margin-top: 2px; }
  .story .none { color: var(--muted); font-size: 13px; }

  /* ---- stat tiles: the number is the chart ---- */

  .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px 24px; }
  .stat-label { color: var(--muted); font-size: 12px; font-weight: 500; }
  .stat-value { color: var(--ink); font-size: 24px; font-weight: 600; letter-spacing: -.02em; margin-top: 2px; line-height: 1.15; font-variant-numeric: tabular-nums; }
  .stat-note { color: var(--muted); font-size: 12px; margin-top: 2px; }

  .meter { height: 4px; border-radius: 999px; background: var(--meter-track); overflow: hidden; margin-top: 18px; }
  .meter > span { display: block; height: 100%; border-radius: 999px; background: var(--meter-fill); }

  /* ---- pills, chips, notes ---- */

  .pill {
    display: inline-block; padding: 1px 7px; border-radius: 4px;
    font-size: 11px; font-weight: 600; letter-spacing: .02em; line-height: 1.6;
    border: 1px solid var(--line); color: var(--ink-soft); background: var(--panel-2);
  }
  .Draft, .Open { border-color: transparent; background: var(--live-bg); color: var(--live-text); }
  .Created, .Promoted { border-color: transparent; background: var(--ok-bg); color: var(--ok-text); }
  .Failed { border-color: transparent; background: var(--err-bg); color: var(--err-text); }

  .chips { display: flex; gap: 4px; flex-wrap: wrap; align-items: center; margin-top: 10px; }
  .chips .label { color: var(--muted); font-size: 12px; margin-right: 2px; }
  .chip {
    display: inline-block; padding: 1px 6px; border-radius: 4px;
    background: var(--panel-2); font-family: var(--mono); font-size: 11.5px; color: var(--ink-soft);
  }

  .note, .warn, .banner {
    border-radius: 6px; padding: 9px 12px; font-size: 13.5px; border: 1px solid;
    background: var(--warn-bg); color: var(--warn-text); border-color: var(--warn-line);
  }
  .warn { margin-top: 10px; }
  .note a, .banner a { color: inherit; font-weight: 600; }
  .banner.err { background: var(--err-bg); color: var(--err-text); border-color: var(--err-line); }
  .banner.ok { background: var(--ok-bg); color: var(--ok-text); border-color: var(--ok-line); }
  .banner ul { margin: 6px 0 0; padding-left: 20px; }
  .banner li { margin-top: 2px; }
  #setup, #banners { display: grid; gap: 10px; }
  #setup:not(:empty), #banners:not(:empty) { margin-bottom: 20px; }

  .status { font-size: 13px; color: var(--ink-soft); margin-top: 10px; min-height: 1.2em; }
  .status:empty { display: none; }
  .status.err { color: var(--err-text); }
  .status.ok { color: var(--ok-text); }

  /* ---- narrow screens ---- */

  @media (max-width: 760px) {
    header.top { gap: 10px; padding: 0 14px; height: auto; min-height: 52px; flex-wrap: wrap; padding-top: 8px; padding-bottom: 8px; }
    header.top .token-wrap { width: 100%; }
    header.top input.token { width: 100%; }
    main { padding: 22px 14px 64px; }
    .card { padding: 15px 16px; }
    .story { grid-template-columns: 1fr; gap: 2px 0; }
    .story .lbl { padding-top: 8px; }
    .story .lbl:first-child { padding-top: 0; }
  }
""";

    /// <summary>The flame in the header. Inline so the pages stay one file each with no assets to serve.</summary>
    public const string Mark = """
<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 22c4.4 0 7-3 7-7 0-3.5-2-5.5-3.5-7.5C15 9 14.5 10.5 13 11c.5-3-.5-6-3-8 0 3.5-2 5-3.5 7C5 11.8 5 13.3 5 15c0 4 2.6 7 7 7z"/></svg>
""";

    /// <summary>The shared header, with the page that is current marked.</summary>
    public static string Header(string current) => $$"""
<header class="top">
  <a class="brand" href="dashboard"><span class="mark">{{Mark.Trim()}}</span>Housekeeper</a>
  <nav>
    <a href="dashboard"{{(current == "dashboard" ? " class=\"here\"" : "")}}>Dashboard</a>
    <a href="noticed"{{(current == "noticed" ? " class=\"here\"" : "")}}>Noticed <span id="nav-badge" class="badge" hidden></span></a>
    <a href="concerns"{{(current == "concerns" ? " class=\"here\"" : "")}}>Concerns</a>
    <a href="settings"{{(current == "settings" ? " class=\"here\"" : "")}}>Settings</a>
    <a href="logs"{{(current == "logs" ? " class=\"here\"" : "")}}>Logs</a>
  </nav>
  <span class="spacer"></span>
  <span class="token-wrap">
    <button id="token-toggle" class="key quiet" type="button" title="API token. Only needed when Housekeeper is reached over the network rather than on this machine." aria-expanded="false">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="8" cy="15" r="4"/><path d="M10.9 12.1 21 2m-3 3 3 3m-6 0 3 3"/></svg>
      <span id="token-label">API token</span>
    </button>
    <input id="token" class="token" type="password" placeholder="Paste the API token" autocomplete="off" hidden />
  </span>
</header>
""";

    /// <summary>
    /// The token box's behaviour, shared by both pages: remembered in local storage, hidden until it is
    /// needed, and labelled so the reader can see at a glance whether one is set.
    /// </summary>
    public const string TokenScript = """
const token = document.getElementById('token');
const tokenToggle = document.getElementById('token-toggle');
const tokenLabel = document.getElementById('token-label');
token.value = localStorage.getItem('housekeeper-token') || '';

function tokenState(needed) {
  const has = token.value.trim().length > 0;
  tokenToggle.classList.toggle('set', has && !needed);
  tokenToggle.classList.toggle('needed', !!needed);
  tokenLabel.textContent = needed ? 'Token needed' : (has ? 'Token set' : 'API token');
}

function showToken(show) {
  token.hidden = !show;
  tokenToggle.setAttribute('aria-expanded', show ? 'true' : 'false');
  if (show) token.focus();
}

tokenToggle.addEventListener('click', () => showToken(token.hidden));
token.addEventListener('input', () => { localStorage.setItem('housekeeper-token', token.value.trim()); tokenState(false); });
token.addEventListener('keydown', (event) => { if (event.key === 'Enter' || event.key === 'Escape') showToken(false); });
tokenState(false);

function headers(json) {
  const h = json ? { 'Content-Type': 'application/json' } : {};
  const t = token.value.trim();
  if (t) h.Authorization = 'Bearer ' + t;
  return h;
}

async function call(path, options) {
  const response = await fetch(path, options);
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = null; }
  if (response.status === 401) { tokenState(true); showToken(true); }
  if (!response.ok) {
    const detail = body && (body.error || (body.errors && body.errors.join(' ')));
    throw new Error(detail || ('HTTP ' + response.status));
  }
  return body;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

/**
 * The count of open findings on the Noticed menu item, on every page. Red when any of them is serious:
 * something the user asked to watch for, an automation of theirs that no longer works, or a finding far
 * past its bar. Quietly absent when the API cannot be reached; the page itself says so.
 */
async function navBadge() {
  const badge = document.getElementById('nav-badge');
  if (!badge) return;
  try {
    const summary = await call('api/anomalies/summary', { headers: headers(false) });
    badge.hidden = !summary.open;
    badge.textContent = String(summary.open);
    badge.classList.toggle('serious', summary.serious > 0);
    badge.title = summary.open + ' open' + (summary.serious ? ', ' + summary.serious + ' serious or asked for' : '');
  } catch { /* leave whatever was there */ }
}
navBadge();
setInterval(navBadge, 30000);

// ---- small words, shared by every page ----

/** 1,284 · 12.9K · 4.2M. Compact only once the digits stop being scannable. */
function compact(n) {
  if (n === null || n === undefined) return '—';
  if (n < 10000) return n.toLocaleString();
  if (n < 1000000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
  return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
}

/** Says a span of seconds the way the server says one, so the two never disagree. */
function spoken(seconds) {
  const s = Math.abs(seconds);
  if (s < 90) return Math.round(s) + ' seconds';
  if (s < 5400) return Math.round(s / 60) + ' minutes';
  if (s < 172800) return (s / 3600).toFixed(1) + ' hours';
  return (s / 86400).toFixed(1) + ' days';
}

/** Seconds from now: negative in the past, positive in the future. */
function fromNow(iso) {
  return iso ? (new Date(iso).getTime() - Date.now()) / 1000 : null;
}

/** "just now" · "4 min ago" · "3 h ago" · "yesterday" · "6 d ago", for the corner of a card. */
function ago(iso) {
  const s = -fromNow(iso);
  if (s === null || isNaN(s)) return '';
  if (s < 45) return 'just now';
  if (s < 3600) return Math.round(s / 60) + ' min ago';
  if (s < 86400) return Math.round(s / 3600) + ' h ago';
  if (s < 172800) return 'yesterday';
  return Math.round(s / 86400) + ' d ago';
}

/** Writes a line of feedback into a card, or clears it. */
function tell(status, message, kind) {
  if (!status) return;
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
}

let toastTimer = null;

/** A short confirmation at the bottom of the viewport, for actions taken on cards far down the page. */
function toast(message, kind) {
  let node = document.getElementById('toast');
  if (!node) { node = el('div', 'toast'); node.id = 'toast'; document.body.append(node); }
  node.textContent = message;
  node.className = 'toast show' + (kind === 'err' ? ' err' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.remove('show'), kind === 'err' ? 8000 : 4500);
}

/** Seconds of waiting before the count appears, so a quick action does not flash a number. */
const COUNT_AFTER = 2;

/** And how long before the wait is worth explaining again, for the ones that go to a local model. */
const PATIENCE_AFTER = 25;

/**
 * Counts the seconds on a busy button, and says something once the wait gets long.
 *
 * A spinner says "working"; it does not say "getting somewhere". Drafting goes to a model that may be a 7B
 * on someone's CPU, so ten to sixty seconds of an unchanging label is normal — and is indistinguishable
 * from a wedged request, which is what it was reported as. A number that climbs is the whole difference.
 *
 * Returns the function that stops it, which the caller must run however the handler ends.
 */
function countUp(button, options, status) {
  const spinner = el('span', 'spin');
  const caption = document.createTextNode(options.busy);
  button.replaceChildren(spinner, caption);

  const started = Date.now();
  let explained = false;

  const tick = () => {
    const seconds = Math.round((Date.now() - started) / 1000);
    caption.textContent = seconds >= COUNT_AFTER ? options.busy + ' ' + seconds + 's' : options.busy;

    if (!explained && status && options.patience && seconds >= PATIENCE_AFTER) {
      explained = true;
      tell(status, options.patience);
    }
  };

  const timer = setInterval(tick, 500);
  tick();
  return () => clearInterval(timer);
}

/**
 * A button that disables every button on its card while its handler runs, holds the card against the
 * background refresh, and then reloads the page's list once, AFTER the hold is released, so the card is
 * rebuilt from what the action changed rather than left showing its old state with the buttons back on.
 *
 * Every button on the same card is disabled, not just the ones beside it: a draft offers Create, Discard
 * and Refine in two rows, and while any of them is in flight the others are still decisions about the
 * same draft. Making two of them is how a live automation ended up recorded as superseded with no id.
 *
 * Options: status (the card's status line, where an error lands), busy and patience (a running caption
 * and what to say when the wait gets long), reload (false to skip the reload, when the handler navigates
 * away), after (run once the reload has happened, with the handler's result -- reveal the new card, say).
 * The page provides refreshPage(); without one, nothing is reloaded.
 */
function action(label, handler, kind, options) {
  const button = el('button', kind === true ? 'primary' : (kind || null), label);
  button.type = 'button';
  const status = options && options.status;
  button.addEventListener('click', async () => {
    const scope = button.closest('.card') || button.parentElement;
    const siblings = scope ? [...scope.querySelectorAll('button')] : [button];
    const held = siblings.filter((other) => !other.disabled);
    held.forEach((other) => { other.disabled = true; });

    const release = hold(button.closest('.card'));
    const stopCounting = options && options.busy ? countUp(button, options, status) : null;
    let ok = false, result;
    try {
      result = await handler();
      ok = true;
    } catch (err) {
      if (status) tell(status, err.message, 'err'); else toast(err.message, 'err');
    } finally {
      if (stopCounting) stopCounting();
      held.forEach((other) => { other.disabled = false; });
      button.textContent = label;

      const stale = release();
      const reload = typeof refreshPage === 'function' ? refreshPage : null;
      if (reload && ok && (!options || options.reload !== false)) {
        await reload();
        if (options && options.after) options.after(result);
      } else if (reload && stale) {
        reload();
      }
    }
  });
  return button;
}

// ---- a block of text with a copy button ----

const COPY_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>';
const COPIED_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>';

/**
 * Puts text on the clipboard. The clipboard API only exists on a secure page, and Home Assistant over plain
 * http on the LAN is not one; an embedded browser may also refuse it outright. Either way the old
 * selection-and-copy route is the fallback, and only if both fail is it reported.
 */
async function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) {
    try { await navigator.clipboard.writeText(text); return; } catch { /* fall through */ }
  }
  const scratch = document.createElement('textarea');
  scratch.value = text;
  scratch.setAttribute('readonly', '');
  scratch.style.position = 'fixed';
  scratch.style.opacity = '0';
  const previous = document.activeElement;
  document.body.append(scratch);
  let ok = false;
  try {
    scratch.select();
    ok = typeof document.execCommand === 'function' && document.execCommand('copy');
  } finally {
    // select() took the focus; put it back so a keyboard user keeps their place in the card.
    scratch.remove();
    if (previous instanceof HTMLElement && previous !== document.body) {
      try { previous.focus({ preventScroll: true }); } catch { /* nothing to restore */ }
    }
  }
  if (!ok) throw new Error('The browser refused to copy.');
}

/** A <pre> of `text` (written with textContent, never markup) with a Copy button in its corner. */
function codeBlock(text, what) {
  const box = el('div', 'code');
  const pre = el('pre', null, text);
  const button = el('button', 'copy');
  button.type = 'button';
  // The title describes; the visible text names the button, so "Copied" is what a screen reader hears.
  button.title = 'Copy ' + (what || 'to clipboard');
  const label = el('span', null, 'Copy');
  label.setAttribute('aria-live', 'polite');
  const setIcon = (svg) => { button.querySelector('svg')?.remove(); button.insertAdjacentHTML('afterbegin', svg); };
  setIcon(COPY_ICON);
  button.append(label);

  let timer = null;
  button.addEventListener('click', async () => {
    try {
      await copyText(text);
      button.classList.add('done'); setIcon(COPIED_ICON); label.textContent = 'Copied';
    } catch (err) {
      label.textContent = 'Could not copy';
      if (typeof toast === 'function') toast(err.message, 'err');
    }
    clearTimeout(timer);
    timer = setTimeout(() => { button.classList.remove('done'); setIcon(COPY_ICON); label.textContent = 'Copy'; }, 1800);
  });

  box.append(pre, button);
  return box;
}

// ---- keeping a list on screen while it is refreshed underneath ----

const prints = new WeakMap();

/**
 * Puts a list of cards into `container`, reusing the card the reader already has whenever the item behind it
 * has not changed. Each entry is { key, print, build, touch }: `key` identifies the item, `print` is a string
 * that changes when anything worth redrawing changes, `build` makes a new node, and `touch` (optional) is
 * called on a reused node for the things that drift without the data changing, such as "2 min ago".
 *
 * A rebuilt card is a new element, and everything the reader had done to the old one goes with it: the YAML
 * they had opened, a refinement half typed, the cursor in a box, a button pressed and still working. The
 * background refresh runs every thirty seconds, so building every card afresh each time meant the page kept
 * undoing what the reader had just done. Now a card is rebuilt only when its data changed; even then what
 * was open, typed, or focused is carried across; a card with an action in flight is left alone until the
 * action finishes; and nodes already in the right place are not moved, so focus and selection survive too.
 */
function reconcile(container, entries) {
  const existing = new Map();
  for (const node of container.children) if (node.dataset.key) existing.set(node.dataset.key, node);

  const active = document.activeElement;
  const wanted = [];
  for (const entry of entries) {
    const key = String(entry.key);
    const old = existing.get(key);
    let node;
    if (old && old.dataset.busy) {
      // Rebuilding under a running action would detach the button whose handler is mid-flight. Keep the card
      // and mark it stale; the action's own completion reloads the list.
      old.dataset.stale = '1';
      node = old;
    } else if (old && prints.get(old) === entry.print) {
      node = old;
      if (entry.touch) entry.touch(node);
    } else {
      node = entry.build();
      node.dataset.key = key;
      prints.set(node, entry.print);
      if (old) carryOver(old, node);
    }
    wanted.push(node);
  }

  wanted.forEach((node, i) => {
    if (container.children[i] !== node) container.insertBefore(node, container.children[i] || null);
  });
  while (container.children.length > wanted.length) container.lastChild.remove();

  // A node that had to move was blurred by the move. Put the reader back where they were.
  if (active && active.isConnected && document.activeElement !== active) {
    try { active.focus({ preventScroll: true }); } catch { /* not focusable any more; fine */ }
  }
}

/** Marks a card so the refresh leaves it alone while an action on it runs. Returns a function that clears it. */
function hold(card) {
  if (!card) return () => false;
  card.dataset.busy = '1';
  return () => {
    delete card.dataset.busy;
    const stale = !!card.dataset.stale;
    delete card.dataset.stale;
    return stale;
  };
}

/**
 * Copies what the reader had done to a card onto its replacement: which sections were open, what was typed
 * into which box, and where the cursor was. Elements are matched by position, which holds because a card is
 * built the same way for the same item.
 */
function carryOver(old, fresh) {
  const pair = (selector) => {
    const before = old.querySelectorAll(selector);
    const after = fresh.querySelectorAll(selector);
    return [...before].map((node, i) => [node, after[i]]).filter(([, b]) => b);
  };

  for (const [a, b] of pair('details')) b.open = a.open;

  const active = document.activeElement;
  for (const [a, b] of pair('input:not([type=button]):not([type=submit]), textarea')) {
    if (a.value && !b.value) b.value = a.value;
    if (a === active) {
      const start = a.selectionStart, end = a.selectionEnd;
      queueMicrotask(() => {
        try { b.focus({ preventScroll: true }); if (start != null) b.setSelectionRange(start, end); } catch { /* fine */ }
      });
    }
  }

  if (active && old.contains(active) && !/^(INPUT|TEXTAREA)$/.test(active.tagName)) {
    const focusable = 'button, a[href], summary, [tabindex]';
    const index = [...old.querySelectorAll(focusable)].indexOf(active);
    const target = index >= 0 ? fresh.querySelectorAll(focusable)[index] : null;
    if (target) queueMicrotask(() => { try { target.focus({ preventScroll: true }); } catch { /* fine */ } });
  }
}
""";
}
