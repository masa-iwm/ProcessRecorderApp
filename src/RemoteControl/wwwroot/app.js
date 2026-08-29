// ProcessRecorderApp remote control UI -- startup. No framework, no third-party script.
//
// Loaded last, so this is the only file that may reach into all the others. What it
// owns: the sign-in and sign-out flow, the `EventSource` subscription and the table
// it redraws, and the one place every listener is attached.
'use strict';

(function () {
  var PRA = window.PRA;
  var core = PRA.core;
  var player = PRA.player;
  var recordings = PRA.recordings;
  var settings = PRA.settings;
  // Not aliased to a local name: `state` is the recorder's state text below.
  var shared = core.state;
  var $ = core.$;
  var text = core.text;
  var status = core.status;
  var describe = core.describe;
  var send = core.send;
  var getJson = core.getJson;
  var allows = core.allows;
  var markWrite = core.markWrite;
  var writeButton = core.writeButton;
  var cell = core.cell;

  var events = null;

  // ---- recorders ----

  function renderRecorders(snapshot) {
    var body = $('recordersBody');
    body.replaceChildren();
    shared.recorderNames = [];

    snapshot.recorders.forEach(function (recorder) {
      shared.recorderNames.push(recorder.name);

      var row = document.createElement('tr');
      cell(row, recorder.name);
      cell(row, state(recorder));
      cell(row, recorder.isRecording);
      cell(row, recorder.lastFilename);
      cell(row, recorder.lastError ? recorder.lastError : 'ok');

      var actions = document.createElement('td');

      var start = writeButton('Start', function () { control('/api/recorders/' + encodeURIComponent(recorder.name) + '/start'); });
      start.className = 'btn-primary';
      actions.appendChild(start);

      var stop = writeButton('Stop', function () { control('/api/recorders/' + encodeURIComponent(recorder.name) + '/stop'); });
      stop.className = 'btn-danger';
      actions.appendChild(stop);

      // Watching is a read, so every role that can see this table can watch:
      // it is not gated on the write roles.
      var preview = document.createElement('button');
      preview.type = 'button';
      preview.textContent = 'Preview';
      preview.addEventListener('click', function () { player.startSelectedPreview(recorder.name); });
      actions.appendChild(preview);

      row.appendChild(actions);

      body.appendChild(row);
    });

    $('startAll').disabled = !snapshot.canStartAll;
    $('stopAll').disabled = !snapshot.canStopAll;

    settings.syncRecorderSelect();
    // `state.recorderNames` was just rewritten, and the recordings filter is drawn
    // from it. Nothing else on that page notices this event.
    recordings.syncRecorderOptions();
    // The auxiliary encoder slots are a property of the process, not of a recorder, and
    // this event is the only thing that carries them: the quality menu of the recording
    // player is what reads them.
    player.onAuxiliaryEncoders(snapshot.auxiliaryEncodersFree);
  }

  function state(recorder) {
    if (!recorder.isInitialized) { return 'uninitialized'; }
    if (recorder.isRecording) { return 'recording'; }
    if (recorder.isAwaitingRecoveryResume) { return 'awaiting-resume'; }
    return recorder.continuousState && recorder.continuousState !== 'off'
      ? 'continuous:' + recorder.continuousState
      : 'idle';
  }

  function control(url) {
    status($('recordersStatus'), 'working...', false);
    send('POST', url).then(function () {
      status($('recordersStatus'), 'ok', false);
    }).catch(function (error) {
      status($('recordersStatus'), error.message, true);
    });
  }

  // ---- live state ----

  // The server already debounces the state events (200ms), so the handler redraws
  // directly. Reconnection is the browser's job for EventSource.
  // The handle is kept so that a 401 can close it: EventSource retries on its own
  // and would otherwise keep hammering an endpoint that is refusing this browser.
  var identityCheckInFlight = false;

  function subscribe() {
    if (events !== null) { return; }
    events = new EventSource('/api/events');
    events.addEventListener('state', function (event) {
      renderRecorders(JSON.parse(event.data));
    });
    // The body of a `recording` event is only "something under the root changed".
    // It is handed over as the signal it is; the list is read back from the API.
    events.addEventListener('recording', function (event) {
      recordings.onRecordingChanged(event);
    });
    // EventSource retries on its own and never says why it failed. An expired tab
    // would therefore reconnect forever, and every attempt writes another
    // `remote.auth fail`. So each error asks once who we are: a 401 means the
    // session is gone (getJson switches to the form, which closes this stream),
    // anything else is left to the built-in retry.
    events.addEventListener('error', function () {
      if (identityCheckInFlight) { return; }
      identityCheckInFlight = true;
      getJson('/api/me').catch(function () { /* a 401 has already switched the page */ })
        .then(function () { identityCheckInFlight = false; });
    });
  }

  // ---- sign in / sign out ----

  // The page that was on screen is not touched here: the hash keeps it, so signing
  // in again comes back to the same one.
  function showLogin(message) {
    player.stopPreview();
    player.stopFollow();
    if (events !== null) { events.close(); events = null; }
    $('mainSections').classList.add('hidden');
    $('identity').classList.add('hidden');
    // The pages behind the form are hidden, so the links that switch between them
    // lead nowhere; only the hash they set survives, and that is kept anyway.
    $('nav').hidden = true;
    $('loginSection').classList.remove('hidden');
    $('loginPassword').value = '';
    // Only a guest has a screen to go back to: with guest reading off, cancelling
    // would land on a page that answers 401 to everything.
    $('loginCancel').hidden = !shared.guest;
    text($('loginError'), message === undefined ? '' : message);
  }

  function showApp() {
    $('loginSection').classList.add('hidden');
    $('mainSections').classList.remove('hidden');
    $('identity').classList.remove('hidden');
    $('nav').hidden = false;
    text($('identityName'), shared.guest ? 'guest (Viewer)' : shared.userName + ' (' + shared.role + ')');
    $('loginButton').hidden = !shared.guest;
    $('logoutButton').hidden = shared.guest;
  }

  // Deliberately not routed through `send`: a 401 here is the answer to this form,
  // not a lost session, so it must stay on this screen with the reason visible.
  function submitLogin() {
    text($('loginError'), 'signing in...');
    fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-PRApp-Client': '1' },
      body: JSON.stringify({ user: $('loginUser').value, password: $('loginPassword').value }),
      credentials: 'same-origin'
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (body) {
        if (!response.ok) { throw new Error(describe(body, response.status)); }
        return body;
      });
    }).then(function () {
      $('loginPassword').value = '';
      text($('loginError'), '');
      start();
    }).catch(function (error) {
      text($('loginError'), error.message);
    });
  }

  function logout() {
    send('POST', '/api/logout').then(function () {
      showLogin('Signed out.');
    }).catch(function (error) {
      showLogin(error.message);
    });
  }

  // The whole page hangs off this one answer: who we are decides both what is
  // drawn and whether the reads are allowed to start at all.
  function start() {
    // Deliberately not routed through `getJson`: that would show the form before
    // `guest` is known, and the form draws its "Cancel" button from it. A 401 here
    // is the answer "reading needs a session", which is exactly `guest === false`.
    fetch('/api/me', { credentials: 'same-origin' }).then(function (response) {
      if (response.status === 401) {
        shared.guest = false;
        showLogin('Sign in to continue.');
        return null;
      }
      return response.json().then(function (body) {
        if (!response.ok) { throw new Error(describe(body, response.status)); }
        return body;
      });
    }).then(function (me) {
      if (me === null) { return; }
      shared.role = me.role;
      shared.guest = me.guest;
      shared.userName = me.name;
      core.applyPermissions();
      showApp();
      subscribe();
      // Only an Admin can use the result, and enumerating monitors and cameras is
      // not free -- so it is not fetched for the roles that cannot apply it.
      if (allows('admin')) { settings.loadSources(); }
      loadCapabilities();
      settings.loadAppSettings();
      settings.loadVariables();
      recordings.loadRecordings();
    }).catch(function () {
      // The 401 is handled above (it decides `guest`). Everything else lands here
      // and goes to the form too, because nothing on the main page can be trusted
      // to have loaded.
      showLogin('Sign in to continue.');
    });
  }

  // What this machine can do. **Read once**: the decoder is probed once per process, and
  // the part that does change (how many encoder slots are free) rides on the SSE `state`.
  // A failure is swallowed on purpose -- the page works without the feature, and an error
  // over a screen that is otherwise fine says nothing anyone can act on.
  function loadCapabilities() {
    getJson('/api/capabilities').then(function (capabilities) {
      shared.capabilities = capabilities;
    }).catch(function () {
      shared.capabilities = { transcode: false, decoder: null, auxiliaryEncoderLimit: 0 };
    });
  }

  // ---- startup ----

  // The core's 401 handling and the player's both switch to the form through here.
  shared.showLogin = showLogin;

  $('startAll').addEventListener('click', function () { control('/api/recorders/start-all'); });
  $('stopAll').addEventListener('click', function () { control('/api/recorders/stop-all'); });
  markWrite($('startAll'));
  markWrite($('stopAll'));

  $('loginSubmit').addEventListener('click', submitLogin);
  $('loginPassword').addEventListener('keydown', function (event) {
    if (event.key === 'Enter') { submitLogin(); }
  });
  $('loginButton').addEventListener('click', function () { showLogin(); });
  // Back to reading as a guest. Routed through `start()` so the page is rebuilt from
  // `/api/me`: the session may have expired while the form was open.
  $('loginCancel').addEventListener('click', function () { start(); });
  $('logoutButton').addEventListener('click', logout);

  $('loadRecorderSettings').addEventListener('click', settings.loadRecorderSettings);
  // The pipeline on screen belongs to the recorder that was loaded. Leaving it
  // there after the selection changes offers another recorder's value for Apply.
  $('recorderSelect').addEventListener('change', function () {
    text($('sourceCurrent'), '');
  });
  markWrite($('applyRecorderSettings'), 'admin').addEventListener('click', settings.applyRecorderSettings);
  $('sourceSelect').addEventListener('change', settings.buildSourceForm);
  markWrite($('applySource'), 'admin').addEventListener('click', settings.applySource);
  $('loadAppSettings').addEventListener('click', settings.loadAppSettings);
  $('loadVariables').addEventListener('click', settings.loadVariables);
  markWrite($('putVariable')).addEventListener('click', settings.putVariable);
  markWrite($('variablePersist'));
  markWrite($('variableKey'));
  markWrite($('variableValue'));
  $('loadRecordings').addEventListener('click', recordings.loadRecordings);
  $('stopPlayer').addEventListener('click', function () { player.stopFollow('stopped'); });
  // The two <video> elements outlive every playback and every connection, so each
  // of these is attached once; what they do is app-player.js's, because that is
  // where the state they read lives.
  $('player').addEventListener('seeking', player.onPlayerSeeking);
  $('player').addEventListener('error', player.onPlayerError);

  $('stopPreview').addEventListener('click', function () { player.stopPreview('stopped'); });
  $('previewMode').addEventListener('change', player.onPreviewModeChange);
  $('previewPlayer').addEventListener('error', player.onPreviewError);
  // Leaving the page has to release the subscription: the seat is only returned
  // when the response ends, and a background tab would hold it indefinitely.
  window.addEventListener('pagehide', function () { player.stopPreview(); player.stopFollow(); });

  start();
})();
