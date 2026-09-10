namespace HearthSense.Api;

/// <summary>
/// The main screen: ask for an automation, review what came back, triage what the scanner noticed.
/// One file, no build step, no dependencies. Every server value is written with textContent or assigned to
/// an input value, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class Dashboard
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>HearthSense</title>
<style>
""" + Ui.Styles + """
  .composer textarea { min-height: 74px; }
  .composer .hint { color: var(--muted); font-size: 12.5px; margin-left: 4px; }
  .refine-row { margin-top: 10px; padding-top: 12px; border-top: 1px dashed var(--line); }
  .refine { flex: 1 1 280px; min-width: 0; }
  .card .actions { margin-top: 14px; }
  .live { color: var(--ok-text); font-size: 13.5px; margin-top: 12px; }
  details { margin-top: 12px; }
  summary.reveal {
    cursor: pointer; font-size: 13px; color: var(--link); width: fit-content;
    padding: 2px 0; list-style-position: inside;
  }
  summary.reveal:hover { text-decoration: underline; }
  details[open] summary.reveal { margin-bottom: 2px; }
  details pre { margin-top: 6px; }
</style>
</head>
<body>
<header class="top">
  <span class="brand">HearthSense</span>
  <nav>
    <a href="dashboard" class="here">Dashboard</a>
    <a href="settings">Settings</a>
  </nav>
  <input id="token" class="token" type="password" placeholder="API token (not needed on loopback)" />
</header>

<main>
  <div id="setup"></div>

  <section class="card composer">
    <h1 class="title">Ask for an automation</h1>
    <p class="lead meta">Describe it in plain English. You will see the YAML, and everything it touches, before anything reaches your home.</p>
    <textarea id="request" rows="3" placeholder="Turn off the lights when no one is home"></textarea>
    <div class="row actions">
      <button id="draft" class="primary" type="button">Draft it</button>
      <button id="scan" type="button">Scan now</button>
      <span class="hint">Nothing goes live until you confirm it.</span>
    </div>
    <div id="status" class="status"></div>
  </section>

  <h2 class="section">Proposals</h2>
  <div id="proposals"></div>

  <h2 class="section">Noticed</h2>
  <div id="anomalies"></div>
</main>

<script>
const $ = (id) => document.getElementById(id);
const token = $('token');
token.value = localStorage.getItem('hearthsense-token') || '';
token.addEventListener('input', () => localStorage.setItem('hearthsense-token', token.value.trim()));

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
  if (!response.ok) throw new Error((body && body.error) || ('HTTP ' + response.status));
  return body;
}

function say(message, kind) {
  const status = $('status');
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

function chips(label, values) {
  const row = el('div', 'chips');
  row.append(el('span', 'label', label));
  for (const value of values) row.append(el('span', 'chip', value));
  return row;
}

function action(label, handler, primary) {
  const button = el('button', primary ? 'primary' : null, label);
  button.type = 'button';
  button.addEventListener('click', async () => {
    button.disabled = true;
    try { await handler(); } catch (err) { say(err.message, 'err'); } finally { button.disabled = false; }
  });
  return button;
}

function proposalCard(p) {
  const card = el('div', 'card');

  const head = el('div', 'card-head');
  head.append(el('span', 'pill ' + p.status, p.status), el('h3', null, p.alias || p.request));
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

  if (p.entities && p.entities.length) card.append(chips('Entities', p.entities));
  if (p.actions && p.actions.length) card.append(chips('Calls', p.actions));
  // A draft is there to be read before you approve it. Anything already decided keeps its YAML folded
  // away so the list stays scannable.
  if (p.yaml) {
    if (p.status === 'Draft') {
      card.append(el('pre', null, p.yaml));
    } else {
      const details = el('details');
      details.append(el('summary', 'reveal', 'Show the automation'), el('pre', null, p.yaml));
      card.append(details);
    }
  }

  if (p.status === 'Draft') {
    const row = el('div', 'row actions');
    row.append(
      action('Create in Home Assistant', async () => {
        await call('api/proposals/' + p.id + '/confirm', { method: 'POST', headers: headers(false) });
        say('Created in Home Assistant.', 'ok'); await refresh();
      }, true),
      action('Discard', async () => {
        await call('api/proposals/' + p.id + '/reject', { method: 'POST', headers: headers(false) });
        say('Discarded.'); await refresh();
      }));
    card.append(row);

    const feedback = el('input', 'refine');
    feedback.type = 'text';
    feedback.placeholder = 'Not quite? Say what should change, e.g. "only the kitchen light"';
    const refineRow = el('div', 'row refine-row');
    refineRow.append(feedback, action('Refine', async () => {
      const text = feedback.value.trim();
      if (!text) { say('Say what should change first.', 'err'); return; }
      say('Asking the model…');
      await call('api/proposals/' + p.id + '/refine',
        { method: 'POST', headers: headers(true), body: JSON.stringify({ feedback: text }) });
      say('Refined. Review the new draft.', 'ok'); await refresh();
    }));
    card.append(refineRow);
  } else if (p.haAutomationId) {
    card.append(el('div', 'live', 'Live in Home Assistant as automation id ' + p.haAutomationId + '.'));
  }

  return card;
}

function anomalyCard(a) {
  const card = el('div', 'card');

  const head = el('div', 'card-head');
  head.append(el('span', 'pill ' + a.status, a.status), el('h3', null, a.entityId));
  card.append(head);

  card.append(el('div', null, a.summary));
  card.append(el('div', 'quote', 'Suggested: ' + a.suggestedRequest));

  if (a.status === 'Open') {
    const row = el('div', 'row actions');
    row.append(
      action('Make an automation', async () => {
        say('Drafting…');
        await call('api/anomalies/' + a.id + '/automate', { method: 'POST', headers: headers(false) });
        say('Drafted. Review it under Proposals.', 'ok'); await refresh();
      }, true),
      action('Dismiss', async () => {
        await call('api/anomalies/' + a.id + '/dismiss', { method: 'POST', headers: headers(false) });
        say('Dismissed.'); await refresh();
      }));
    card.append(row);
  }

  return card;
}

function render(container, items, build, emptyText) {
  container.replaceChildren();
  if (!items || !items.length) { container.append(el('div', 'empty', emptyText)); return; }
  for (const item of items) container.append(build(item));
}

async function checkSetup() {
  const setup = $('setup');
  setup.replaceChildren();
  try {
    const status = await call('api/status', { headers: headers(false) });
    if (status && status.ready === false) {
      const note = el('div', 'note');
      note.append(document.createTextNode('HearthSense has no Home Assistant token yet, so nothing can be drafted. '));
      const link = el('a', null, 'Open settings');
      link.href = 'settings';
      note.append(link, document.createTextNode(' to add one.'));
      setup.append(note);
    }
  } catch { /* the banner is a nicety; never let it break the page */ }
}

async function refresh() {
  const [proposals, anomalies] = await Promise.all([
    call('api/proposals?limit=25', { headers: headers(false) }),
    call('api/anomalies?limit=25', { headers: headers(false) }),
  ]);
  render($('proposals'), proposals, proposalCard, 'Nothing drafted yet. Describe an automation above.');
  render($('anomalies'), anomalies, anomalyCard,
    'Nothing unusual so far. Findings appear once there is enough history to compare against.');
}

$('draft').addEventListener('click', async () => {
  const request = $('request').value.trim();
  if (!request) { say('Describe what you want first.', 'err'); return; }

  const button = $('draft');
  button.disabled = true;
  say('Asking the model…');
  try {
    await call('api/proposals', { method: 'POST', headers: headers(true), body: JSON.stringify({ request }) });
    $('request').value = '';
    say('Drafted. Review it below.', 'ok');
  } catch (err) {
    say(err.message, 'err');
  } finally {
    button.disabled = false;
    await refresh().catch(() => {});
  }
});

$('scan').addEventListener('click', async () => {
  const button = $('scan');
  button.disabled = true;
  say('Scanning…');
  try {
    const report = await call('api/scan', { method: 'POST', headers: headers(false) });
    say('Observed ' + report.observed + ' entities and raised ' + report.raised + '.', 'ok');
    await refresh();
  } catch (err) {
    say(err.message, 'err');
  } finally {
    button.disabled = false;
  }
});

checkSetup();
refresh().catch((err) => say(err.message, 'err'));
setInterval(() => refresh().catch(() => {}), 30000);
</script>
</body>
</html>
""";
}
