namespace HearthSense.Api;

/// <summary>
/// The stylesheet both pages share. One place, so the dashboard and the settings screen cannot drift apart.
///
/// Every colour is a variable with a light and a dark value, and every form control sets its own background
/// and text colour. That matters: declaring <c>color-scheme: light dark</c> without styling the controls
/// leaves the browser painting inputs from the dark palette on top of a light page.
/// </summary>
internal static class Ui
{
    public const string Styles = """
  *, *::before, *::after { box-sizing: border-box; }

  :root {
    color-scheme: light dark;

    --bg: #f5f3f0;
    --panel: #fffefd;
    --panel-2: #faf8f5;
    --ink: #1c1917;
    --ink-soft: #57534e;
    --muted: #79716b;
    --line: #e7e3dd;
    --line-soft: #f1eeea;

    --brand: #7c2d12;
    --brand-2: #9a3412;
    --on-brand: #ffffff;
    --link: #9a3412;
    --focus: #c2410c;

    --field: #ffffff;
    --field-line: #d7d1c9;

    --ok-bg: #dcfce7; --ok-ink: #14532d; --ok-line: #86efac; --ok-text: #15803d;
    --warn-bg: #fef3c7; --warn-ink: #78350f; --warn-line: #fcd34d;
    --err-bg: #fee2e2; --err-ink: #7f1d1d; --err-line: #fca5a5; --err-text: #b91c1c;
    --info-bg: #dbeafe; --info-ink: #1e3a8a;
    --neutral-bg: #ece9e4; --neutral-ink: #44403c;

    --radius: 10px;
    --shadow: 0 1px 2px rgba(28, 25, 23, .04), 0 1px 3px rgba(28, 25, 23, .06);
  }

  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #161412;
      --panel: #211e1b;
      --panel-2: #1a1816;
      --ink: #f5f1ec;
      --ink-soft: #c7c0b8;
      --muted: #9b938a;
      --line: #332e29;
      --line-soft: #2a2622;

      --brand: #9a3412;
      --brand-2: #b45309;
      --link: #fb923c;
      --focus: #fb923c;

      --field: #191715;
      --field-line: #403931;

      --ok-bg: #06301a; --ok-ink: #bbf7d0; --ok-line: #166534; --ok-text: #4ade80;
      --warn-bg: #3a2a06; --warn-ink: #fde68a; --warn-line: #a16207;
      --err-bg: #3f1414; --err-ink: #fecaca; --err-line: #b91c1c; --err-text: #fca5a5;
      --info-bg: #13233f; --info-ink: #bfdbfe;
      --neutral-bg: #2b2724; --neutral-ink: #d6d3d1;

      --shadow: 0 1px 2px rgba(0, 0, 0, .35), 0 1px 3px rgba(0, 0, 0, .25);
    }
  }

  body {
    margin: 0;
    background: var(--bg);
    color: var(--ink);
    font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
    font-size: 15px;
    line-height: 1.5;
    -webkit-font-smoothing: antialiased;
  }

  /* ---- header ---- */

  header.top {
    position: sticky; top: 0; z-index: 20;
    display: flex; align-items: center; gap: 14px; flex-wrap: wrap;
    padding: 11px 20px;
    background: linear-gradient(180deg, var(--brand), var(--brand-2));
    color: var(--on-brand);
    box-shadow: 0 1px 0 rgba(0, 0, 0, .14), 0 2px 10px rgba(0, 0, 0, .06);
  }
  header.top .brand { font-size: 17px; font-weight: 700; letter-spacing: -.01em; }
  header.top nav { display: flex; gap: 4px; }
  header.top nav a {
    color: #fde8d7; text-decoration: none; font-size: 14px;
    padding: 5px 12px; border-radius: 999px; transition: background .12s ease, color .12s ease;
  }
  header.top nav a:hover { background: rgba(255, 255, 255, .13); color: #fff; }
  header.top nav a.here { background: rgba(255, 255, 255, .18); color: #fff; font-weight: 600; }
  header.top input.token {
    margin-left: auto; min-width: 250px; width: auto;
    background: rgba(255, 255, 255, .12);
    border: 1px solid rgba(255, 255, 255, .24);
    color: #fff; padding: 7px 11px; font-size: 13.5px;
  }
  header.top input.token::placeholder { color: rgba(255, 255, 255, .62); }
  header.top input.token:focus { background: rgba(255, 255, 255, .18); }

  /* ---- layout ---- */

  main { max-width: 960px; margin: 0 auto; padding: 26px 20px 72px; }
  h1.title { font-size: 19px; margin: 0 0 4px; letter-spacing: -.01em; }
  h2.section {
    font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: .08em;
    color: var(--muted); margin: 30px 0 10px;
  }
  p.lead { margin: 0 0 14px; }

  /* ---- controls ---- */

  input, textarea, select, button { font: inherit; }
  input[type=text], input[type=password], input[type=number], input[type=search], textarea, select {
    width: 100%; padding: 9px 12px; border-radius: 8px;
    background: var(--field); color: var(--ink);
    border: 1px solid var(--field-line);
    transition: border-color .12s ease, box-shadow .12s ease;
  }
  input[type=text]:hover, input[type=password]:hover, input[type=number]:hover, textarea:hover, select:hover {
    border-color: var(--muted);
  }
  textarea { resize: vertical; line-height: 1.55; }
  input[type=checkbox] { width: 17px; height: 17px; accent-color: var(--brand-2); margin: 4px 0 0; }
  :focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }

  button {
    cursor: pointer; padding: 9px 15px; border-radius: 8px; font-weight: 500;
    background: var(--panel); color: var(--ink); border: 1px solid var(--field-line);
    transition: background .12s ease, border-color .12s ease;
  }
  button:hover:not(:disabled) { background: var(--panel-2); border-color: var(--muted); }
  button:disabled { opacity: .45; cursor: default; }
  button.primary { background: var(--brand); border-color: var(--brand); color: var(--on-brand); }
  button.primary:hover:not(:disabled) { background: var(--brand-2); border-color: var(--brand-2); }
  button.link {
    background: none; border: none; padding: 0 2px; font-size: 13px;
    color: var(--link); text-decoration: underline; font-weight: 500;
  }
  button.link:hover:not(:disabled) { background: none; }

  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .row.actions { margin-top: 12px; }

  /* ---- cards ---- */

  .card {
    background: var(--panel); border: 1px solid var(--line); border-radius: var(--radius);
    padding: 16px 18px; margin-bottom: 14px; box-shadow: var(--shadow);
  }
  .card-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; margin-bottom: 6px; }
  .card-head h3 { margin: 0; font-size: 16.5px; letter-spacing: -.01em; }
  .meta { color: var(--ink-soft); font-size: 13.5px; }
  .meta + .meta { margin-top: 3px; }
  .quote { color: var(--ink-soft); font-size: 13.5px; font-style: italic; }

  .empty {
    color: var(--muted); font-size: 14px; text-align: center;
    padding: 22px; border: 1px dashed var(--line); border-radius: var(--radius); background: var(--panel-2);
  }

  pre {
    background: var(--panel-2); border: 1px solid var(--line); border-radius: 8px;
    padding: 12px 14px; margin: 12px 0 0; overflow-x: auto;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: 12.5px; line-height: 1.55; color: var(--ink);
  }

  /* ---- pills, chips, notes ---- */

  .pill {
    display: inline-block; padding: 3px 9px; border-radius: 999px;
    font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em;
  }
  .Draft, .Open { background: var(--info-bg); color: var(--info-ink); }
  .Created, .Promoted { background: var(--ok-bg); color: var(--ok-ink); }
  .Rejected, .Dismissed, .Superseded { background: var(--neutral-bg); color: var(--neutral-ink); }
  .Failed { background: var(--err-bg); color: var(--err-ink); }

  .chips { display: flex; gap: 5px; flex-wrap: wrap; align-items: center; margin-top: 9px; }
  .chips .label { color: var(--muted); font-size: 12.5px; margin-right: 2px; }
  .chip {
    display: inline-block; padding: 2px 8px; border-radius: 6px;
    background: var(--panel-2); border: 1px solid var(--line);
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px;
    color: var(--ink-soft);
  }

  .note, .warn, .banner {
    border-radius: 8px; padding: 10px 13px; font-size: 14px; border: 1px solid;
    background: var(--warn-bg); color: var(--warn-ink); border-color: var(--warn-line);
  }
  .warn { margin-top: 10px; }
  .note a, .banner a { color: inherit; font-weight: 600; }
  .banner.err { background: var(--err-bg); color: var(--err-ink); border-color: var(--err-line); }
  .banner.ok { background: var(--ok-bg); color: var(--ok-ink); border-color: var(--ok-line); }
  .banner ul { margin: 6px 0 0; padding-left: 20px; }
  .banner li { margin-top: 2px; }
  #setup, #banners { display: grid; gap: 10px; }
  #setup:not(:empty), #banners:not(:empty) { margin-bottom: 18px; }

  .status { font-size: 13.5px; color: var(--ink-soft); margin-top: 10px; min-height: 1.2em; }
  .status:empty { display: none; }
  .status.err { color: var(--err-text); }
  .status.ok { color: var(--ok-text); }

  /* ---- narrow screens ---- */

  @media (max-width: 760px) {
    header.top { gap: 10px; padding: 10px 14px; }
    header.top input.token { margin-left: 0; width: 100%; min-width: 0; }
    main { padding: 18px 14px 64px; }
  }
""";
}
