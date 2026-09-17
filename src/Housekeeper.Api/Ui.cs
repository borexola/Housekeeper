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
""";
}
