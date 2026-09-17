namespace Housekeeper.Api;

/// <summary>
/// The main screen: ask for an automation, review what came back, triage what the scanner noticed.
/// One file, no build step, no dependencies. Every server value is written with textContent or assigned to
/// an input value, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class Dashboard
{
    public static readonly string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Housekeeper</title>
<style>
""" + Ui.Styles + """

  /* ---- the composer ---- */

  .composer h1.title { font-size: 22px; }
  .composer .lead { max-width: 600px; color: var(--muted); }
  .composer textarea { min-height: 96px; font-size: 15px; padding: 12px 14px; }
  .composer .row.actions { margin-top: 10px; }
  .composer .hint { color: var(--muted); font-size: 12.5px; }

  .suggest { margin-top: 16px; }
  .suggest .label { color: var(--muted); font-size: 12.5px; margin-bottom: 6px; }
  .suggest .try { display: flex; gap: 6px; flex-wrap: wrap; }
  .suggest .try button {
    padding: 4px 10px; font-size: 12.5px; font-weight: 400; border-radius: 999px;
    color: var(--ink-soft); text-align: left; line-height: 1.4;
  }
  .suggest .try button:hover:not(:disabled) { color: var(--ink); border-color: var(--ink-soft); background: var(--panel); }

  /* ---- watching ---- */

  .insight .meta { margin-top: 12px; }

  /* ---- proposal cards ---- */

  .refine-row { margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--line-soft); }
  .refine { flex: 1 1 280px; min-width: 0; }
  .card .actions { margin-top: 14px; }
  .card.dismissed, .card.closed { opacity: .6; }
  .card.dismissed:hover, .card.closed:hover { opacity: 1; }
  .live { color: var(--ok-text); font-size: 13px; margin-top: 12px; }
  details { margin-top: 12px; }
  summary.reveal { cursor: pointer; font-size: 12.5px; color: var(--muted); width: fit-content; list-style: none; }
  summary.reveal::-webkit-details-marker { display: none; }
  summary.reveal::before { content: "+ "; }
  details[open] summary.reveal::before { content: "− "; }
  summary.reveal:hover { color: var(--ink); }
  details pre { margin-top: 8px; }
  .safety { color: var(--muted); font-size: 12.5px; margin-top: 10px; }

  /* The card an action just produced is scrolled to and outlined so the eye lands on it. */
  .card.flash { animation: flash 1.6s ease-out; }
  @keyframes flash { 0% { border-color: var(--accent); } 100% { border-color: var(--line); } }

  .spin {
    display: inline-block; width: 10px; height: 10px; margin-right: 2px; vertical-align: -1px;
    border: 1.5px solid currentColor; border-right-color: transparent; border-radius: 50%;
    animation: spin .8s linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }

  /* Feedback for an action taken far down the page, where the composer's status line is out of sight. */
  .toast {
    position: fixed; left: 50%; bottom: 22px; transform: translateX(-50%);
    background: var(--ink); color: var(--bg); padding: 9px 14px; border-radius: 6px;
    font-size: 13px; max-width: 90vw; z-index: 10;
    opacity: 0; transition: opacity .15s; pointer-events: none;
  }
  .toast.show { opacity: 1; }
  .toast.err { background: var(--err-text); color: #fff; }

  .skeleton { height: 72px; border-radius: var(--radius); border: 1px solid var(--line-soft); }
</style>
</head>
<body>
""" + Ui.Header("dashboard") + """

<main>
  <div id="stale" class="banner" hidden></div>
  <div id="setup"></div>

  <h2 class="section" style="margin-top:0">Watching <span class="tools"><button id="scan" class="small" type="button">Scan now</button></span></h2>
  <div id="insight" class="insight"><div class="skeleton"></div></div>

  <section class="composer" style="margin-top:44px">
    <h1 class="title">What should your home do?</h1>
    <p class="lead meta">Describe it in plain English. You will see exactly what it would do, and the YAML, before anything reaches Home Assistant.</p>
    <textarea id="request" rows="3" placeholder="Turn off the lights when no one is home"></textarea>
    <div class="row actions">
      <button id="draft" class="primary" type="button">Draft it</button>
      <span class="hint">Ctrl+Enter to draft. Nothing goes live until you confirm it.</span>
    </div>
    <div id="suggest" class="suggest" hidden>
      <div class="label">Or start from something in your home</div>
      <div class="try"></div>
    </div>
    <div id="status" class="status"></div>
  </section>

  <h2 class="section">Proposals <span id="proposal-count" class="count" hidden></span>
    <span class="tools"><span class="segmented" id="proposal-filter"><button type="button" class="on" data-v="active">Active</button><button type="button" data-v="all">All</button></span></span>
  </h2>
  <div id="proposals"><div class="skeleton"></div></div>

</main>

<script>
const $ = (id) => document.getElementById(id);
""" + Ui.TokenScript + """

function say(message, kind) {
  const status = $('status');
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
}

// ---- numbers people can read ----

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

/** "00:05:00" or "14.00:00:00" as seconds. */
function span(text) {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(text || '');
  return m ? (+(m[1] || 0)) * 86400 + (+m[2]) * 3600 + (+m[3]) * 60 + (+m[4]) : 0;
}

function stat(label, value, note, id) {
  const tile = el('div', 'stat');
  if (id) tile.id = id;
  tile.append(el('div', 'stat-label', label), el('div', 'stat-value', value));
  if (note) tile.append(el('div', 'stat-note', note));
  return tile;
}

// ---- the parts that age ----
//
// Three pieces of the panel are clocks: the countdown to the next scan, how long ago the last one ran, and
// how far back the history reaches. Re-rendering the card every second would fight anything the reader is
// doing with it, so instead the last payload is kept and only these three strings are rewritten in place.

let insight = null;
let insightDue = false;

function nextScanText(i) {
  if (!i.scanning) return 'Off';
  const next = fromNow(i.nextUtc);
  if (next === null) return 'Soon';
  return next <= 0 ? 'Due now' : 'in ' + spoken(next);
}

function keptNoteText(i) {
  return i.history.oldestUtc ? 'over the last ' + spoken(fromNow(i.history.oldestUtc)) : 'none yet';
}

function lastScanText(i) {
  if (!i.last) return 'No scan has run yet. The first one runs shortly after startup.';

  const when = spoken(fromNow(i.last.atUtc));
  if (i.last.error) return 'The last scan failed ' + when + ' ago: ' + i.last.error;

  return 'Last scan ' + when + ' ago took ' + (i.last.tookMs / 1000).toFixed(1) + 's: '
    + compact(i.last.newSamples) + ' new changes stored'
    + (i.last.backfilled ? ' plus ' + compact(i.last.backfilled) + ' read from Home Assistant\'s history' : '') + ', '
    + (i.last.raised ? i.last.raised + ' new finding' + (i.last.raised === 1 ? '' : 's') : 'nothing new')
    + (i.last.resolved ? ', ' + i.last.resolved + ' closed by itself' : '')
    + ' · ' + compact(i.last.visible) + ' entities visible in Home Assistant.';
}

/** Writes only when the text actually changed, so a reader's selection survives the tick. */
function retext(selector, text) {
  const node = document.querySelector(selector);
  if (node && node.textContent !== text) node.textContent = text;
}

function tickInsight() {
  if (!insight) return;

  retext('#tile-next .stat-value', nextScanText(insight));
  retext('#tile-kept .stat-note', keptNoteText(insight));
  retext('#last-scan', lastScanText(insight));

  // The moment the scheduled scan falls due, collect its result rather than waiting for the next poll.
  const next = fromNow(insight.nextUtc);
  if (insight.scanning && next !== null && next <= 0 && !insightDue) {
    insightDue = true;
    setTimeout(quietRefresh, 4000);
  }
}

/** What the last scan could actually form an opinion about. Null until a scan has run. */
function judged(i) {
  return i.last && i.last.judged !== null && i.last.judged !== undefined ? i.last.judged : null;
}

function insightCard(i) {
  insight = i;
  // A scan that has run moves the deadline forward; only then is it worth watching for the next one.
  if (fromNow(i.nextUtc) > 0) insightDue = false;

  const card = el('div', 'card');
  // Nullish, not falsy: a scan that watched nothing reports 0, and treating that as "no answer" fell back
  // to the count of entities with stored history -- what USED to be watched -- and stated it as fact.
  const watched = (i.last && i.last.observed != null) ? i.last.observed : i.history.entities;
  const ready = judged(i);

  const stats = el('div', 'stats');
  stats.append(
    stat('Watching', compact(watched),
      i.watching.length ? (i.watching.includes('*') ? 'every entity' : i.watching.join(', ')) : 'nothing selected yet'),
    stat('Changes kept', compact(i.history.samples), keptNoteText(i), 'tile-kept'),
    stat('Ready to judge', ready === null ? '—' : compact(ready), 'enough history for a detector', 'tile-ready'),
    stat('Next scan', nextScanText(i),
      i.scanning ? 'every ' + spoken(span(i.interval)) : 'scanning is turned off', 'tile-next'));
  card.append(stats);

  // How far this install is from being able to say anything at all.
  if (watched > 0 && ready !== null) {
    const meter = el('div', 'meter');
    const fill = el('span');
    fill.style.width = Math.round(Math.min(1, ready / watched) * 100) + '%';
    meter.append(fill);
    card.append(meter);

    card.append(el('div', 'meta', ready === 0
      ? 'No watched entity has changed often enough for a detector to reach a verdict yet. Housekeeper stays quiet until it knows what normal looks like, usually a day or two.'
      : ready >= watched
        ? 'Every watched entity has enough history for a detector to reach a verdict.'
        : compact(ready) + ' of ' + compact(watched) + ' watched entities have enough history for a detector to '
          + 'reach a verdict. The rest have not changed often enough yet.'));
  }

  if (!i.watching.length) {
    card.append(el('div', 'warn', 'Nothing is being watched, so nothing can be noticed. Choose entities under Settings → Watching.'));
  } else if (i.last && i.last.observed === 0) {
    card.append(el('div', 'warn', 'The last scan matched no entities at all. What is under Settings → Watching does not match '
      + 'anything in this Home Assistant, so nothing is being recorded and nothing can be noticed.'));
  } else if (i.last && i.maxTracked && i.last.observed >= i.maxTracked && i.last.visible > i.last.observed) {
    // Watching everything is the default, so on a house bigger than the cap this is the normal case rather
    // than an edge one. The cap takes entities in id order, so it does not pick a sensible subset -- it picks
    // an alphabetical one. Saying so beats leaving someone to wonder why half their house is never mentioned.
    card.append(el('div', 'warn', 'Only ' + compact(i.last.observed) + ' of ' + compact(i.last.visible)
      + ' entities are being watched: the maximum tracked entities cap has been reached, and entities are '
      + 'taken in name order, so the rest are never looked at. Raise the cap under Settings → Watching, or '
      + 'add Ignore globs to narrow what is watched.'));
  }

  const scanned = el('div', i.last && i.last.error ? 'warn' : 'meta', lastScanText(i));
  scanned.id = 'last-scan';
  card.append(scanned);

  return card;
}

/** How many chips a row shows before the rest fold behind "and N more". */
const CHIPS_SHOWN = 6;

function chips(label, values) {
  const row = el('div', 'chips');
  row.append(el('span', 'label', label));
  const shown = values.length > CHIPS_SHOWN + 1 ? values.slice(0, CHIPS_SHOWN) : values;
  for (const value of shown) row.append(el('span', 'chip', value));

  if (shown.length < values.length) {
    const rest = values.slice(shown.length);
    const more = el('button', 'link', 'and ' + rest.length + ' more');
    more.type = 'button';
    more.addEventListener('click', () => {
      for (const value of rest) row.insertBefore(el('span', 'chip', value), more);
      more.textContent = 'show fewer';
      more.replaceWith(fewer);
    });
    const fewer = el('button', 'link', 'show fewer');
    fewer.type = 'button';
    fewer.addEventListener('click', () => {
      for (const chip of [...row.querySelectorAll('.chip')].slice(shown.length)) chip.remove();
      fewer.replaceWith(more);
    });
    row.append(more);
  }
  return row;
}

let toastTimer = null;

/** A short confirmation at the bottom of the viewport, for actions taken on cards far below the composer. */
function toast(message, kind) {
  let node = $('toast');
  if (!node) { node = el('div', 'toast'); node.id = 'toast'; document.body.append(node); }
  node.textContent = message;
  node.className = 'toast show' + (kind === 'err' ? ' err' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.remove('show'), kind === 'err' ? 8000 : 4500);
}

/** Writes a line of feedback into a card, or clears it. */
function tell(status, message, kind) {
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
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
 * from a wedged request, which is what it was reported as. A number that climbs is the whole difference:
 * it turns "is this broken?" into "it has been twelve seconds, and it said ten to sixty".
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
 * A button that disables itself while its handler runs. `busy` is the label to show meanwhile (with a
 * spinner and a running count); `patience` is what to say if the wait gets long; `status` is a card's own
 * status line, so an error lands next to the button that caused it.
 *
 * Every button on the same CARD is disabled for the duration, not just the ones beside it. A draft offers
 * three decisions about itself -- Create, Discard, Refine -- and they do not share a row: Refine sits in its
 * own, and it is the slow one, announcing a 10-60 second wait. While any of them is in flight the others are
 * still decisions about the same draft, and making two of them is how a live automation ended up recorded
 * as superseded with no id.
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

    const stopCounting = options && options.busy ? countUp(button, options, status) : null;
    try {
      await handler();
    } catch (err) {
      if (status) tell(status, err.message, 'err'); else toast(err.message, 'err');
    } finally {
      if (stopCounting) stopCounting();
      held.forEach((other) => { other.disabled = false; });
      button.textContent = label;
    }
  });
  return button;
}

/** Scrolls a proposal card into view and outlines it, so "review it" has an obvious target. */
function reveal(proposalId) {
  const node = document.getElementById('proposal-' + proposalId);
  if (!node) return;
  node.scrollIntoView({ behavior: 'smooth', block: 'center' });
  node.classList.remove('flash');
  void node.offsetWidth;
  node.classList.add('flash');
}

/**
 * An in-page link to a proposal card. The href is real: an anchor without one is not focusable, has no link
 * role, and ignores Enter -- so "review and confirm it" looked exactly like a link and could not be used by
 * anyone on a keyboard or a screen reader. The handler only adds the smooth scroll and the flash; a hash
 * target also stays inside the Home Assistant ingress path, unlike an absolute one.
 */
function jump(text, proposalId) {
  const link = el('a', 'jump', text);
  link.href = '#proposal-' + proposalId;
  link.addEventListener('click', (event) => { event.preventDefault(); reveal(proposalId); });
  return link;
}

// ---- proposals ----

/** The automation in three rows: when, only if, then. What most people check instead of the YAML. */
function storyBlock(story) {
  const block = el('div', 'story');
  const row = (label, items, noneText) => {
    block.append(el('div', 'lbl', label));
    const list = el('ul');
    if (!items || !items.length) list.append(el('li', 'none', noneText));
    else for (const item of items) list.append(el('li', null, item));
    block.append(list);
  };
  row('When', story.when, 'nothing starts it');
  if (story.onlyIf && story.onlyIf.length) row('Only if', story.onlyIf);
  row('Then', story.then, 'nothing happens');
  return block;
}

function proposalCard(p) {
  const card = el('div', p.dismissedUtc ? 'card dismissed' : 'card');
  card.id = 'proposal-' + p.id;

  const head = el('div', 'card-head');
  head.append(el('span', 'pill ' + p.status, p.status), el('h3', null, p.alias || p.request));
  const when = el('span', 'when', ago(p.createdUtc));
  when.title = new Date(p.createdUtc).toLocaleString();
  head.append(when);
  card.append(head);

  if (p.description) card.append(el('div', 'meta', p.description));
  card.append(el('div', 'quote', 'Asked: ' + p.request));
  if (p.feedback) card.append(el('div', 'quote', 'Refined from #' + p.parentId + ': ' + p.feedback));
  if (p.error) card.append(el('div', 'warn', p.error));

  for (const duplicate of p.duplicates || []) {
    const pct = Math.round(duplicate.score * 100);
    card.append(el('div', 'warn',
      'Possible duplicate of "' + duplicate.alias + '" (' + pct + '% similar). ' + duplicate.reason));
  }

  if (p.story) card.append(storyBlock(p.story));
  if (p.entities && p.entities.length) card.append(chips('Entities', p.entities));
  if (p.actions && p.actions.length) card.append(chips('Calls', p.actions));

  // The story above is derived from the same config; the YAML is there for anyone who wants the exact text.
  if (p.yaml) {
    const details = el('details');
    details.append(el('summary', 'reveal', 'YAML'), el('pre', null, p.yaml));
    card.append(details);
  }

  if (p.status === 'Draft') {
    card.append(el('div', 'safety', 'This is exactly what would be created. Nothing reaches Home Assistant until you confirm it.'));

    const status = el('div', 'status');
    const row = el('div', 'row actions');
    row.append(
      action('Create in Home Assistant', async () => {
        await call('api/proposals/' + p.id + '/confirm', { method: 'POST', headers: headers(false) });
        toast('Created in Home Assistant: ' + (p.alias || 'automation') + '.');
        await refresh(); reveal(p.id);
      }, true, { busy: 'Creating…', patience: 'Still writing to Home Assistant. Do not press it again: this is the one step that changes your home.', status }),
      action('Discard', async () => {
        await call('api/proposals/' + p.id + '/reject', { method: 'POST', headers: headers(false) });
        toast('Draft discarded.'); await refresh();
      }, 'quiet danger', { status }));
    card.append(row);

    const feedback = el('input', 'refine');
    feedback.type = 'text';
    feedback.placeholder = 'Not quite? Say what should change, e.g. "only the kitchen light"';
    const refineRow = el('div', 'row refine-row');
    const refine = action('Refine', async () => {
      const text = feedback.value.trim();
      if (!text) { tell(status, 'Say what should change first.', 'err'); return; }
      tell(status, 'Asking the model for a new draft. This usually takes 10–60 seconds.');
      const refined = await call('api/proposals/' + p.id + '/refine',
        { method: 'POST', headers: headers(true), body: JSON.stringify({ feedback: text }) });
      toast('Refined. Review the new draft.');
      await refresh(); reveal(refined.id);
    }, false, { busy: 'Refining…', patience: 'Still going. A model running locally on a CPU can take a minute or more; it has not stalled.', status });
    feedback.addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); refine.click(); } });
    refineRow.append(feedback, refine);
    card.append(refineRow, status);
  } else {
    if (p.status === 'Created' && p.haAutomationId) {
      card.append(el('div', 'live', 'Live in Home Assistant as automation id ' + p.haAutomationId + '.'));
    } else if (p.status === 'Removed') {
      card.append(el('div', 'meta', 'Deleted in Home Assistant after it was created here.'));
    }

    // Anything already decided can be put away. It is only hidden: a live automation keeps running and
    // keeps being watched for entities that vanish under it.
    const status = el('div', 'status');
    const row = el('div', 'row actions');

    row.append(p.dismissedUtc
      ? action('Show again', async () => {
          await call('api/proposals/' + p.id + '/restore', { method: 'POST', headers: headers(false) });
          toast('Back in the list.'); await refresh();
        }, 'small', { status })
      : action('Dismiss', async () => {
          await call('api/proposals/' + p.id + '/dismiss', { method: 'POST', headers: headers(false) });
          toast(p.status === 'Created'
            ? 'Hidden. The automation is still live in Home Assistant.'
            : 'Hidden.');
          await refresh();
        }, 'quiet small', { status }));

    card.append(row, status);
  }

  return card;
}

function emptyState(title, text) {
  const node = el('div', 'empty');
  node.append(el('strong', null, title), document.createTextNode(text));
  return node;
}

function render(container, items, build, empty) {
  container.replaceChildren();
  if (!items || !items.length) { container.append(empty); return; }
  for (const item of items) container.append(build(item));
}

function setCount(id, n, live) {
  const node = $(id);
  node.hidden = !n;
  node.textContent = String(n);
  node.classList.toggle('live', !!live);
}

async function checkSetup() {
  const setup = $('setup');
  setup.replaceChildren();
  try {
    const status = await call('api/status', { headers: headers(false) });
    if (status && status.missing && status.missing.length) {
      const note = el('div', 'note');
      note.append(document.createTextNode('Housekeeper needs ' + status.missing.join(' and ') + ' before it can draft. '));
      const link = el('a', null, 'Open settings');
      link.href = 'settings';
      note.append(link, document.createTextNode('.'));
      setup.append(note);
    }
  } catch { /* the banner is a nicety; never let it break the page */ }
}

/** Example requests written from this house's own entities. Loaded once; silently absent if nothing answers. */
async function loadSuggestions() {
  try {
    const body = await call('api/suggestions', { headers: headers(false) });
    const list = (body && body.suggestions) || [];
    if (!list.length) return;

    const box = $('suggest');
    const row = box.querySelector('.try');
    row.replaceChildren();
    for (const text of list) {
      const chip = el('button', null, text);
      chip.type = 'button';
      chip.addEventListener('click', () => { $('request').value = text; $('request').focus(); });
      row.append(chip);
    }
    box.hidden = false;
  } catch { /* nothing to suggest is fine */ }
}

let showDismissed = false;

async function refresh() {
  const [proposals, insight] = await Promise.all([
    call('api/proposals?limit=25&dismissed=' + showDismissed, { headers: headers(false) }),
    call('api/insight', { headers: headers(false) }),
  ]);

  $('insight').replaceChildren(insightCard(insight));

  // A draft is a decision waiting to be made, so it goes above whatever is merely history. The rest keep
  // the server's order, newest first.
  const rank = (p) => (p.status === 'Draft' ? 0 : 1);
  (proposals || []).sort((x, y) => rank(x) - rank(y));

  const drafts = (proposals || []).filter((p) => p.status === 'Draft').length;
  setCount('proposal-count', showDismissed ? (proposals || []).length : drafts, drafts > 0);
  render($('proposals'), proposals, proposalCard, showDismissed
    ? emptyState('Nothing here', 'No proposals, dismissed or otherwise.')
    : emptyState('Nothing drafted yet', 'Describe an automation above, or make one from a finding under Noticed.'));

  // A card named in the address bar, from the Noticed page or a shared link, is scrolled to and outlined.
  const wanted = /^#proposal-(\d+)$/.exec(location.hash);
  if (wanted && !revealedHash) { revealedHash = true; reveal(+wanted[1]); }
}
let revealedHash = false;

/**
 * Background refreshes fail silently by design -- a blip should not throw an error in someone's face. But a
 * dashboard that has stopped updating looks exactly like one where nothing is happening, while the countdown
 * carries on ticking off a stale payload. After two failures in a row it says so, and clears as soon as one
 * succeeds.
 */
let refreshFailures = 0;
let lastGood = null;

/** Clears the stale banner. Called after ANY successful reload, not only the background poll. */
function succeeded() {
  refreshFailures = 0;
  lastGood = new Date();

  const banner = $('stale');
  if (banner) banner.hidden = true;
}

function quietRefresh() {
  return refresh().then(() => {
    succeeded();
  }).catch(() => {
    refreshFailures += 1;
    if (refreshFailures < 2) return;

    const banner = $('stale');
    if (!banner) return;
    // The age of the data, not the moment it failed to refresh.
    banner.textContent = lastGood
      ? 'Cannot reach Housekeeper. Everything below is as it was at ' + lastGood.toLocaleTimeString() + '.'
      : 'Cannot reach Housekeeper. Nothing below has loaded.';
    banner.hidden = false;
  });
}

async function draft() {
  const request = $('request').value.trim();
  if (!request) { say('Describe what you want first.', 'err'); $('request').focus(); return; }

  const button = $('draft');
  button.disabled = true;
  const stopCounting = countUp(button, { busy: 'Drafting…', patience: 'Still going. A model running locally on a CPU can take a minute or more; it has not stalled.' }, $('status'));
  say('Asking the model. This usually takes 10–60 seconds.');

  let drafted = null;
  try {
    drafted = await call('api/proposals', { method: 'POST', headers: headers(true), body: JSON.stringify({ request }) });
    $('request').value = '';
    say('Drafted. Review it below.', 'ok');
  } catch (err) {
    say(err.message, 'err');
  } finally {
    stopCounting();
    button.disabled = false;
    button.textContent = 'Draft it';

    // Reported rather than swallowed: "Drafted. Review it below." over a list that did not reload points at
    // something that is not there. And only on the path that actually drafted -- this used to run whatever
    // happened, so a failed draft had its real error overwritten by a message claiming one was created.
    await quietRefresh().catch((err) => {
      if (drafted) say('Drafted, but the list could not be reloaded: ' + err.message, 'err');
    });
    if (drafted) reveal(drafted.id);
  }
}

$('draft').addEventListener('click', draft);
$('request').addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') { event.preventDefault(); draft(); }
});

$('scan').addEventListener('click', async () => {
  const button = $('scan');
  button.disabled = true;
  const stopCounting = countUp(button, { busy: 'Scanning…' }, null);
  try {
    const report = await call('api/scan', { method: 'POST', headers: headers(false) });
    toast('Scanned ' + compact(report.observed) + ' watched entities: ' + compact(report.newSamples) + ' new changes recorded, '
      + (report.raised ? report.raised + ' new finding' + (report.raised === 1 ? '' : 's') + '.' : 'nothing new.'));
    await refresh();
    succeeded();
  } catch (err) {
    toast(err.message, 'err');
  } finally {
    stopCounting();
    button.disabled = false;
    button.textContent = 'Scan now';
  }
});

function segmented(id, onChange) {
  const box = $(id);
  box.addEventListener('click', (event) => {
    const button = event.target.closest('button');
    if (!button || button.classList.contains('on')) return;
    for (const other of box.querySelectorAll('button')) other.classList.toggle('on', other === button);
    onChange(button.dataset.v);
  });
}

segmented('proposal-filter', (v) => {
  showDismissed = v === 'all';
  refresh().then(succeeded).catch((err) => say(err.message, 'err'));
});

checkSetup();
loadSuggestions();
refresh().then(succeeded).catch((err) => say(err.message, 'err'));
setInterval(quietRefresh, 30000);
setInterval(tickInsight, 1000);

// A hidden tab has its timers throttled, which is right — nobody is reading it. Coming back to one should
// not show a clock that stopped, so the moment it is visible again the times are corrected and the panel
// refetched rather than waiting for the next tick.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  tickInsight();
  quietRefresh();
});
</script>
</body>
</html>
""";
}
