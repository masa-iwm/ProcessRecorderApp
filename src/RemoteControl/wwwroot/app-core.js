// ProcessRecorderApp remote control UI -- shared core. No framework, no third-party script.
//
// No secret is held here. A session cookie is set either by opening the page once
// with `?token=<token>` or by `POST /api/login`, and every request just says
// `credentials: 'same-origin'`. A 401 therefore means "this browser has no session",
// and the only repair the page can offer is the sign-in form.
//
// What each role may do is decided by the server; this file only decides what to
// draw. `GET /api/me` is the single source for that, so the controls are switched
// in exactly one place (`applyPermissions`).
//
// The five scripts share one global, `PRA`, and are loaded in the order they are
// written in index.html. **This file may not reference the later ones**: it is the
// bottom of the stack, so anything it has to hand back up (the sign-in switch) is
// late-bound through `PRA.core.state`.
'use strict';

(function () {
  var PRA = window.PRA || (window.PRA = {});

  // Everything more than one file reads. The role starts at 'Viewer' until
  // /api/me answers -- starting permissive would flash controls that the server is
  // going to refuse -- and `guest` starts at false because with guest reading off
  // that call answers 401, and starting at true would put a "Cancel" button on the
  // sign-in form that leads nowhere.
  var state = {
    role: 'Viewer',
    guest: false,
    userName: '',
    recorderNames: [],
    // What this machine can do (`GET /api/capabilities`), read once at start-up. It
    // starts at "cannot transcode" for the same reason the role starts at 'Viewer':
    // drawing a menu entry that the server is going to refuse is worse than drawing it a
    // moment late. Only the answer replaces it, so a refused or failed read leaves the
    // page working without the feature.
    capabilities: { transcode: false, decoder: null, auxiliaryEncoderLimit: 0 },
    // Assigned by app.js. Called from here and from app-player.js when a 401 says
    // the session is gone.
    showLogin: null
  };

  function $(id) { return document.getElementById(id); }

  function text(node, value) { node.textContent = value === null || value === undefined ? '' : String(value); }

  function status(node, message, isError) {
    text(node, message);
    node.className = isError ? 'status error' : 'status';
  }

  // The sign-in switch itself lives in app.js (it owns the SSE handle and the
  // players it has to close). Routing every caller through this one forwarder is
  // what keeps this file from referencing a later one.
  function showLogin(message) {
    if (state.showLogin !== null) { state.showLogin(message); }
  }

  // ---- theme ----

  var THEME_KEY = 'prapp.theme';

  // null means "no choice has been made": the attribute is left off the document
  // and the media query in app.css decides.
  var theme = null;

  function readStoredTheme() {
    try {
      var stored = localStorage.getItem(THEME_KEY);
      return stored === 'light' || stored === 'dark' ? stored : null;
    } catch (error) {
      return null;  // storage can be refused outright; the OS setting still works
    }
  }

  function effectiveTheme() {
    if (theme !== null) { return theme; }
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function applyTheme() {
    if (theme === null) {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', theme);
    }
  }

  // The button offers the scheme that is not on screen, so it shows the opposite
  // of the effective one.
  function paintThemeIcon() {
    var icon = $('themeIcon');
    if (icon !== null) { icon.setAttribute('href', effectiveTheme() === 'dark' ? '#i-sun' : '#i-moon'); }
  }

  function toggleTheme() {
    theme = effectiveTheme() === 'dark' ? 'light' : 'dark';
    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch (error) {
      /* the choice is not remembered, but this page still switches */
    }
    applyTheme();
    paintThemeIcon();
  }

  // Only the state is read here. The attribute is already on the document: the
  // inline script in index.html applies the stored choice before the first paint,
  // which is the whole reason it is inline, so applying it a second time here would
  // change nothing.
  theme = readStoredTheme();

  // ---- pages ----

  // The three pages of the application. The sections inside them are built once and
  // stay in the document: this router hides a page, it never removes one, so a
  // preview or a playback keeps running while another page is on screen.
  var ROUTES = [
    { hash: '#/live', page: 'pageLive', link: 'navLive' },
    { hash: '#/recordings', page: 'pageRecordings', link: 'navRecordings' },
    { hash: '#/settings', page: 'pageSettings', link: 'navSettings' }
  ];

  function router() {
    var index = -1;
    for (var i = 0; i < ROUTES.length; i++) {
      if (ROUTES[i].hash === location.hash) { index = i; }
    }

    // An empty or unknown hash becomes the first page. Replaced rather than
    // assigned: an address that leads nowhere must not be left in the history.
    if (index < 0) {
      index = 0;
      history.replaceState(null, '', ROUTES[0].hash);
    }

    for (var j = 0; j < ROUTES.length; j++) {
      $(ROUTES[j].page).hidden = j !== index;
      var link = $(ROUTES[j].link);
      if (j === index) {
        link.setAttribute('aria-current', 'page');
      } else {
        link.removeAttribute('aria-current');
      }
    }
  }

  // ---- requests ----

  function getJson(url) {
    return fetch(url, { credentials: 'same-origin' }).then(function (response) {
      if (response.status === 401) { showLogin('Sign in to continue.'); }
      return response.json().then(function (body) {
        if (!response.ok) { throw new Error(describe(body, response.status)); }
        return body;
      });
    });
  }

  // Every write goes through here so that the headers (and the 401 handling)
  // exist in exactly one place.
  function send(method, url, body) {
    return fetch(url, {
      method: method,
      headers: { 'Content-Type': 'application/json', 'X-PRApp-Client': '1' },
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'same-origin'
    }).then(function (response) {
      if (response.status === 401) { showLogin('Sign in to continue.'); }
      return response.json().catch(function () { return {}; }).then(function (parsed) {
        if (!response.ok) { throw new Error(describe(parsed, response.status)); }
        return parsed;
      });
    });
  }

  function describe(body, statusCode) {
    if (body && typeof body.error === 'string' && body.error.length > 0) { return body.error; }
    return 'HTTP ' + statusCode;
  }

  // ---- roles ----

  // The server's rule, mirrored for drawing only: Viewer < Operator < Admin.
  function allows(need) {
    if (need === 'admin') { return state.role === 'Admin'; }
    return state.role === 'Admin' || state.role === 'Operator';
  }

  // Buttons are hidden and fields are disabled: a hidden field would leave its
  // label pointing at nothing, and a disabled button reads as "temporarily busy".
  function applyPermissions() {
    var controls = document.querySelectorAll('[data-need]');
    for (var i = 0; i < controls.length; i++) { permit(controls[i]); }
    // The source form is hidden as a whole rather than disabled: it is not an
    // "edit this value" control but a template builder, and half of it greyed out
    // says nothing useful to a Viewer. Independent of the page it sits on.
    $('sourceSection').hidden = !allows('admin');
  }

  function permit(node) {
    var ok = allows(node.dataset.need);
    if (node.tagName === 'BUTTON') { node.hidden = !ok; } else { node.disabled = !ok; }
    return node;
  }

  function writeButton(label, onClick, level) {
    var button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.addEventListener('click', onClick);
    return markWrite(button, level);
  }

  function markWrite(node, level) {
    node.dataset.need = level || 'operator';
    return permit(node);
  }

  function cell(row, value) {
    var td = document.createElement('td');
    text(td, value);
    row.appendChild(td);
    return td;
  }

  function formatSize(bytes) {
    if (bytes < 1024) { return bytes + ' B'; }
    if (bytes < 1024 * 1024) { return (bytes / 1024).toFixed(1) + ' KiB'; }
    return (bytes / (1024 * 1024)).toFixed(1) + ' MiB';
  }

  window.addEventListener('hashchange', router);
  $('themeToggle').addEventListener('click', toggleTheme);
  paintThemeIcon();
  router();

  PRA.core = {
    state: state,
    $: $,
    text: text,
    status: status,
    showLogin: showLogin,
    router: router,
    getJson: getJson,
    send: send,
    describe: describe,
    allows: allows,
    applyPermissions: applyPermissions,
    permit: permit,
    writeButton: writeButton,
    markWrite: markWrite,
    cell: cell,
    formatSize: formatSize
  };
})();
