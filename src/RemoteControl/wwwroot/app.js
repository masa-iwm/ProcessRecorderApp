// ProcessRecorderApp remote control UI. No framework, no third-party script.
//
// No secret is held here. A session cookie is set either by opening the page once
// with `?token=<token>` or by `POST /api/login`, and every request just says
// `credentials: 'same-origin'`. A 401 therefore means "this browser has no session",
// and the only repair the page can offer is the sign-in form.
//
// What each role may do is decided by the server; this file only decides what to
// draw. `GET /api/me` is the single source for that, so the controls are switched
// in exactly one place (`applyPermissions`).
'use strict';

(function () {
  // 'Viewer' until /api/me answers. Starting permissive would flash controls that
  // the server is going to refuse.
  var role = 'Viewer';
  var guest = true;
  var userName = '';
  var events = null;

  function $(id) { return document.getElementById(id); }

  function text(node, value) { node.textContent = value === null || value === undefined ? '' : String(value); }

  function status(node, message, isError) {
    text(node, message);
    node.className = isError ? 'status error' : 'status';
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
    if (need === 'admin') { return role === 'Admin'; }
    return role === 'Admin' || role === 'Operator';
  }

  // Buttons are hidden and fields are disabled: a hidden field would leave its
  // label pointing at nothing, and a disabled button reads as "temporarily busy".
  function applyPermissions() {
    var controls = document.querySelectorAll('[data-need]');
    for (var i = 0; i < controls.length; i++) { permit(controls[i]); }
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

  // ---- recorders ----

  var recorderNames = [];

  function renderRecorders(snapshot) {
    var body = $('recordersBody');
    body.replaceChildren();
    recorderNames = [];

    snapshot.recorders.forEach(function (recorder) {
      recorderNames.push(recorder.name);

      var row = document.createElement('tr');
      cell(row, recorder.name);
      cell(row, state(recorder));
      cell(row, recorder.isRecording);
      cell(row, recorder.lastFilename);
      cell(row, recorder.lastError ? recorder.lastError : 'ok');

      var actions = document.createElement('td');
      actions.appendChild(writeButton('Start', function () { control('/api/recorders/' + encodeURIComponent(recorder.name) + '/start'); }));
      actions.appendChild(writeButton('Stop', function () { control('/api/recorders/' + encodeURIComponent(recorder.name) + '/stop'); }));

      // Watching is a read, so every role that can see this table can watch:
      // it is not gated on the write roles.
      var preview = document.createElement('button');
      preview.type = 'button';
      preview.textContent = 'Preview';
      preview.addEventListener('click', function () { startPreview(recorder.name); });
      actions.appendChild(preview);

      row.appendChild(actions);

      body.appendChild(row);
    });

    $('startAll').disabled = !snapshot.canStartAll;
    $('stopAll').disabled = !snapshot.canStopAll;

    syncRecorderSelect();
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

  function syncRecorderSelect() {
    var select = $('recorderSelect');
    var previous = select.value;
    select.replaceChildren();
    recorderNames.forEach(function (name) {
      var option = document.createElement('option');
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    });
    if (recorderNames.indexOf(previous) >= 0) { select.value = previous; }
  }

  // ---- recorder settings (form generated from the property descriptions) ----

  var recorderFields = [];

  // The recorder the form on screen was built from. The apply must go to this one,
  // not to whatever the select happens to show now: the values in the fields belong
  // to the recorder that was loaded.
  var recorderFormId = '';

  function loadRecorderSettings() {
    var id = $('recorderSelect').value;
    if (!id) { return; }
    status($('recorderSettingsStatus'), 'loading...', false);
    getJson('/api/recorders/' + encodeURIComponent(id) + '/settings').then(function (settings) {
      buildRecorderForm(settings);
      recorderFormId = id;
      status($('recorderSettingsStatus'), 'loaded ' + id, false);
    }).catch(function (error) {
      status($('recorderSettingsStatus'), error.message, true);
    });
  }

  function buildRecorderForm(settings) {
    var host = $('recorderSettingsForm');
    host.replaceChildren();
    recorderFields = [];

    var groups = new Map();
    settings.properties.forEach(function (property) {
      var category = property.category || '';
      if (!groups.has(category)) { groups.set(category, []); }
      groups.get(category).push(property);
    });

    groups.forEach(function (properties, category) {
      var group = document.createElement('div');
      group.className = 'group';
      var heading = document.createElement('h3');
      text(heading, category);
      group.appendChild(heading);

      properties.forEach(function (property) {
        group.appendChild(buildField(property, settings.values[property.name]));
      });

      host.appendChild(group);
    });
  }

  function buildField(property, current) {
    var field = document.createElement('div');
    field.className = 'field';

    var label = document.createElement('label');
    text(label, property.name);
    if (property.description) { label.title = property.description; }
    field.appendChild(label);

    var input;
    if (property.type === 'enum' && property.choices) {
      input = document.createElement('select');
      property.choices.forEach(function (choice) {
        var option = document.createElement('option');
        option.value = choice;
        option.textContent = choice;
        input.appendChild(option);
      });
      input.value = current === null || current === undefined ? '' : String(current);
    } else if (property.type === 'bool') {
      input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = current === true;
    } else if (property.type === 'int') {
      input = document.createElement('input');
      input.type = 'number';
      if (property.min !== null && property.min !== undefined) { input.min = String(property.min); }
      if (property.max !== null && property.max !== undefined) { input.max = String(property.max); }
      input.value = current === null || current === undefined ? '' : String(current);
    } else {
      input = document.createElement('input');
      input.type = 'text';
      input.value = current === null || current === undefined ? '' : String(current);
    }

    if (property.description) { input.title = property.description; }
    markWrite(input, 'admin');
    field.appendChild(input);

    if (property.requiresReinitialize) {
      var note = document.createElement('span');
      note.className = 'status';
      text(note, 'needs reinitialize');
      field.appendChild(note);
    }

    recorderFields.push({ property: property, input: input, original: current, field: field });
    return field;
  }

  function readField(entry) {
    if (entry.property.type === 'bool') { return entry.input.checked; }
    if (entry.property.type === 'int') {
      return entry.input.value === '' ? null : Number(entry.input.value);
    }
    return entry.input.value;
  }

  // Only the keys whose value actually moved are sent: a full PATCH would rewrite
  // settings that another client changed between the load and the apply.
  function applyRecorderSettings() {
    var id = recorderFormId;
    if (!id) {
      status($('recorderSettingsStatus'), 'load a recorder first', true);
      return;
    }

    var patch = {};
    var count = 0;

    recorderFields.forEach(function (entry) {
      var value = readField(entry);
      // An emptied number field is not a value: the server has no null for an int,
      // and "clear this" is not something the form can express.
      if (value === null) { return; }
      var before = entry.original === undefined ? null : entry.original;
      if (String(value) !== String(before === null ? '' : before)) {
        patch[entry.property.name] = value;
        count++;
      }
    });

    if (count === 0) {
      status($('recorderSettingsStatus'), 'nothing changed', false);
      return;
    }

    send('PATCH', '/api/recorders/' + encodeURIComponent(id) + '/settings', patch).then(function (result) {
      var parts = ['applied: ' + result.applied.join(', ')];
      if (result.clamped && result.clamped.length > 0) { parts.push('clamped: ' + result.clamped.join(', ')); }
      if (result.requiresReinitialize && result.requiresReinitialize.length > 0) {
        parts.push('needs reinitialize: ' + result.requiresReinitialize.join(', '));
      }
      status($('recorderSettingsStatus'), parts.join(' / '), false);
      loadRecorderSettings();
    }).catch(function (error) {
      status($('recorderSettingsStatus'), error.message, true);
    });
  }

  // ---- app settings ----

  function loadAppSettings() {
    status($('appSettingsStatus'), 'loading...', false);
    getJson('/api/settings').then(function (settings) {
      var host = $('appSettingsForm');
      host.replaceChildren();

      Object.keys(settings).forEach(function (key) {
        var field = document.createElement('div');
        field.className = 'field';

        var label = document.createElement('label');
        text(label, key);
        field.appendChild(label);

        var input = markWrite(document.createElement('input'), 'admin');
        input.type = 'text';
        input.value = settings[key] === null || settings[key] === undefined ? '' : String(settings[key]);
        field.appendChild(input);

        field.appendChild(writeButton('Apply', function () {
          patchAppSetting(key, input.value);
        }, 'admin'));

        host.appendChild(field);
      });

      status($('appSettingsStatus'), Object.keys(settings).length + ' keys', false);
    }).catch(function (error) {
      status($('appSettingsStatus'), error.message, true);
    });
  }

  // One key per request: the server rejects the whole body when a single value is
  // wrong, so batching would make an unrelated typo discard the rest.
  function patchAppSetting(key, raw) {
    var body = {};
    body[key] = coerce(raw);
    send('PATCH', '/api/settings', body).then(function (result) {
      status($('appSettingsStatus'), 'applied: ' + result.applied.join(', '), false);
    }).catch(function (error) {
      status($('appSettingsStatus'), error.message, true);
    });
  }

  // The app settings endpoint has no property descriptions, so the text is mapped
  // back to a JSON type by shape. Anything that is not a number or a boolean stays
  // a string and the server answers with a 400 the caller can read.
  function coerce(raw) {
    if (raw === 'true') { return true; }
    if (raw === 'false') { return false; }
    if (raw !== '' && !isNaN(Number(raw))) { return Number(raw); }
    return raw;
  }

  // ---- variables ----

  function loadVariables() {
    status($('variablesStatus'), 'loading...', false);
    getJson('/api/variables').then(function (result) {
      var body = $('variablesBody');
      body.replaceChildren();
      result.variables.forEach(function (variable) {
        var row = document.createElement('tr');
        cell(row, variable.key);
        cell(row, variable.value);
        cell(row, variable.persistent);
        body.appendChild(row);
      });
      status($('variablesStatus'), result.variables.length + ' variables', false);
    }).catch(function (error) {
      status($('variablesStatus'), error.message, true);
    });
  }

  function putVariable() {
    var key = $('variableKey').value;
    if (!key) {
      status($('variablesStatus'), 'key is required', true);
      return;
    }
    send('PUT', '/api/variables/' + encodeURIComponent(key), {
      value: $('variableValue').value,
      persist: $('variablePersist').checked
    }).then(function () {
      status($('variablesStatus'), 'ok', false);
      loadVariables();
    }).catch(function (error) {
      status($('variablesStatus'), error.message, true);
    });
  }

  // ---- recordings ----

  // Each segment is escaped on its own: the separators are part of the route and
  // must survive, everything else in a filename may not be URL-safe.
  function encodePath(path) {
    return path.split('/').map(encodeURIComponent).join('/');
  }

  function formatSize(bytes) {
    if (bytes < 1024) { return bytes + ' B'; }
    if (bytes < 1024 * 1024) { return (bytes / 1024).toFixed(1) + ' KiB'; }
    return (bytes / (1024 * 1024)).toFixed(1) + ' MiB';
  }

  function loadRecordings() {
    getJson('/api/recordings').then(function (result) {
      text($('recordingsRoot'), result.root + ' (' + result.files.length + ' files)');

      var body = $('recordingsBody');
      body.replaceChildren();

      result.files.forEach(function (file) {
        var url = '/api/recordings/' + encodePath(file.path);

        var row = document.createElement('tr');
        cell(row, file.path);
        cell(row, formatSize(file.length));
        cell(row, new Date(file.lastWriteTimeUtc).toLocaleString());
        cell(row, file.inProgress ? 'recording' : '');

        var actions = document.createElement('td');

        var play = document.createElement('button');
        play.type = 'button';
        play.textContent = 'Play';
        // A file that is still being written has no finished moov box, so it cannot
        // be played; the bytes are downloadable all the same.
        play.disabled = file.inProgress;
        play.addEventListener('click', function () {
          var player = $('player');
          player.src = url;
          player.play().catch(function () { /* the browser decides; the controls remain */ });
        });
        actions.appendChild(play);

        var download = document.createElement('a');
        download.href = url + '?download=1';
        download.download = '';
        download.textContent = 'Download';
        actions.appendChild(download);

        row.appendChild(actions);
        body.appendChild(row);
      });
    }).catch(function (error) {
      status($('recordingsRoot'), error.message, true);
    });
  }

  // ---- live preview (fragmented MP4 over MSE) ----
  //
  // The response is an endless chunked body: one init segment (ftyp+moov) and then
  // moof+mdat pairs. `<video src>` cannot be pointed at that, so the bytes are fed
  // to a MediaSource by hand. `mode = 'sequence'` puts a late joiner's first
  // fragment at zero, which is what makes joining mid-stream work at all.
  //
  // A second init segment never arrives on one connection (the server closes the
  // response instead), so every restart builds a fresh MediaSource: appending a new
  // init to the SourceBuffer that already has one is exactly what breaks playback.
  //
  // Every way this can go wrong ends in the same place: abort, say why, and rebuild
  // from a fresh MediaSource a second later. A SourceBuffer that has rejected one
  // append never takes the next one, so there is nothing to salvage in place.

  var PREVIEW_MIME = 'video/mp4; codecs="avc1.4d401f"';

  // How much of the past to keep in the SourceBuffer. Without this the buffer grows
  // for as long as the page is open and the browser eventually refuses to append.
  var PREVIEW_WINDOW_SECONDS = 30;

  // Trimming starts only once the buffered span is this long, and then cuts back to
  // PREVIEW_WINDOW_SECONDS. The hysteresis is not cosmetic: without it every
  // `updateend` would call `remove()` again, `remove()` raises `updateend`, and the
  // SourceBuffer would stay `updating` forever -- `appendBuffer` never gets a turn
  // and the picture freezes with the network still running.
  var PREVIEW_TRIM_TRIGGER_SECONDS = 35;

  // Playing further behind the live edge than this means the tab was throttled;
  // seeking forward is the only way back (the stream has no seekable timeline).
  var PREVIEW_LAG_SECONDS = 3;

  var PREVIEW_RECONNECT_MS = 1000;

  // The most undelivered body we will hold. Reached only when appends cannot keep
  // up with the network, which no amount of further buffering fixes.
  var PREVIEW_MAX_QUEUE_BYTES = 16 * 1024 * 1024;

  // Bumped by every stop and every start. Callbacks that belong to an older
  // generation return without touching anything: an aborted fetch, a pending
  // timer and an in-flight `updateend` can all outlive the stream they came from.
  var previewGeneration = 0;
  var previewAbort = null;
  var previewTimer = null;
  var previewUrl = null;

  // The active pump's failure hook. The <video> element outlives every connection,
  // so its `error` listener is attached once and routed through here.
  var previewOnFailure = null;

  function previewSupported() {
    return typeof MediaSource !== 'undefined' && MediaSource.isTypeSupported(PREVIEW_MIME);
  }

  function releasePreviewUrl() {
    if (previewUrl !== null) {
      URL.revokeObjectURL(previewUrl);
      previewUrl = null;
    }
  }

  function stopPreview(message) {
    previewGeneration++;
    previewOnFailure = null;
    if (previewTimer !== null) {
      clearTimeout(previewTimer);
      previewTimer = null;
    }
    if (previewAbort !== null) {
      previewAbort.abort();
      previewAbort = null;
    }

    var video = $('previewPlayer');
    video.removeAttribute('src');
    video.load();
    releasePreviewUrl();

    status($('previewStatus'), message === undefined ? '' : message, false);
  }

  function startPreview(id) {
    stopPreview('');
    if (!previewSupported()) {
      status($('previewStatus'), 'this browser cannot play ' + PREVIEW_MIME, true);
      return;
    }
    status($('previewStatus'), 'connecting to ' + id + '...', false);
    connectPreview(id, previewGeneration);
  }

  function connectPreview(id, generation) {
    var video = $('previewPlayer');
    var source = new MediaSource();
    var controller = new AbortController();
    previewAbort = controller;

    releasePreviewUrl();
    previewUrl = URL.createObjectURL(source);
    video.src = previewUrl;

    source.addEventListener('sourceopen', function () {
      if (generation !== previewGeneration) { return; }

      var buffer;
      try {
        buffer = source.addSourceBuffer(PREVIEW_MIME);
      } catch (error) {
        status($('previewStatus'), error.message, true);
        return;
      }
      buffer.mode = 'sequence';
      pumpPreview(id, generation, controller, buffer);
    });
  }

  function pumpPreview(id, generation, controller, buffer) {
    var video = $('previewPlayer');
    var queue = [];
    var queued = 0;
    var broken = false;

    // One place decides that this connection is unusable, so no caller has to
    // reason about whether a retry is still allowed.
    function fail(reason) {
      if (broken || generation !== previewGeneration) { return; }
      broken = true;
      previewOnFailure = null;
      controller.abort();
      reconnectPreview(id, generation, reason);
    }

    previewOnFailure = fail;

    function flush() {
      if (broken || generation !== previewGeneration || buffer.updating || queue.length === 0) { return; }

      // Peek, append, then drop. Shifting first loses the chunk when appendBuffer
      // throws, and a byte stream with a hole in it never recovers.
      var chunk = queue[0];
      try {
        buffer.appendBuffer(chunk);
      } catch (error) {
        fail('append failed: ' + error.message);
        return;
      }
      queue.shift();
      queued -= chunk.byteLength;
    }

    buffer.addEventListener('error', function () { fail('the source buffer failed'); });

    buffer.addEventListener('updateend', function () {
      if (broken || generation !== previewGeneration) { return; }
      trimPreview(video, buffer);
      followPreview(video);
      flush();
    });

    fetch('/api/recorders/' + encodeURIComponent(id) + '/preview.mp4', {
      signal: controller.signal,
      credentials: 'same-origin'
    }).then(function (response) {
      if (!response.ok) {
        // 404 and 503 are answers, not hiccups: reconnecting would just repeat them.
        return response.json().catch(function () { return {}; }).then(function (body) {
          throw new Error(describe(body, response.status));
        });
      }

      status($('previewStatus'), 'streaming ' + id, false);
      var reader = response.body.getReader();

      function read() {
        return reader.read().then(function (chunk) {
          if (broken || generation !== previewGeneration) { return undefined; }
          if (chunk.done) {
            reconnectPreview(id, generation, 'reconnecting to ' + id + '...');
            return undefined;
          }

          queue.push(chunk.value);
          queued += chunk.value.byteLength;
          if (queued > PREVIEW_MAX_QUEUE_BYTES) {
            fail('the player fell too far behind (' + formatSize(queued) + ' unread)');
            return undefined;
          }

          flush();
          return read();
        });
      }

      return read();
    }).catch(function (error) {
      if (broken || generation !== previewGeneration || error.name === 'AbortError') { return; }
      previewOnFailure = null;
      status($('previewStatus'), error.message, true);
    });
  }

  function trimPreview(video, buffer) {
    if (buffer.updating) { return; }
    var ranges = video.buffered;
    if (ranges.length === 0) { return; }

    var start = ranges.start(0);
    var end = ranges.end(ranges.length - 1);

    // Compare the span, not the end. `end` passes 30 once and stays past it, so a
    // test against `end` alone would trim on every single updateend forever.
    if (end - start <= PREVIEW_TRIM_TRIGGER_SECONDS) { return; }

    try {
      buffer.remove(start, end - PREVIEW_WINDOW_SECONDS);
    } catch (error) {
      /* the next updateend tries again */
    }
  }

  function followPreview(video) {
    var ranges = video.buffered;
    if (ranges.length === 0) { return; }

    var end = ranges.end(ranges.length - 1);
    if (end - video.currentTime > PREVIEW_LAG_SECONDS) { video.currentTime = end - 0.5; }
  }

  function reconnectPreview(id, generation, message) {
    if (generation !== previewGeneration || previewTimer !== null) { return; }
    status($('previewStatus'), message, false);

    previewTimer = setTimeout(function () {
      previewTimer = null;
      if (generation !== previewGeneration) { return; }
      connectPreview(id, generation);
    }, PREVIEW_RECONNECT_MS);
  }

  // ---- live state ----

  // The server already debounces the state events (200ms), so the handler redraws
  // directly. Reconnection is the browser's job for EventSource.
  // The handle is kept so that a 401 can close it: EventSource retries on its own
  // and would otherwise keep hammering an endpoint that is refusing this browser.
  function subscribe() {
    if (events !== null) { return; }
    events = new EventSource('/api/events');
    events.addEventListener('state', function (event) {
      renderRecorders(JSON.parse(event.data));
    });
  }

  // ---- sign in / sign out ----

  function showLogin(message) {
    stopPreview();
    if (events !== null) { events.close(); events = null; }
    $('mainSections').classList.add('hidden');
    $('identity').classList.add('hidden');
    $('loginSection').classList.remove('hidden');
    $('loginPassword').value = '';
    text($('loginError'), message === undefined ? '' : message);
  }

  function showApp() {
    $('loginSection').classList.add('hidden');
    $('mainSections').classList.remove('hidden');
    $('identity').classList.remove('hidden');
    text($('identityName'), guest ? 'guest (Viewer)' : userName + ' (' + role + ')');
    $('loginButton').hidden = !guest;
    $('logoutButton').hidden = guest;
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
    getJson('/api/me').then(function (me) {
      role = me.role;
      guest = me.guest;
      userName = me.name;
      applyPermissions();
      showApp();
      subscribe();
      loadAppSettings();
      loadVariables();
      loadRecordings();
    }).catch(function () {
      // A 401 has already switched the page to the form; anything else lands there
      // too, because nothing on the main page can be trusted to have loaded.
      showLogin('Sign in to continue.');
    });
  }

  // ---- startup ----

  $('startAll').addEventListener('click', function () { control('/api/recorders/start-all'); });
  $('stopAll').addEventListener('click', function () { control('/api/recorders/stop-all'); });
  markWrite($('startAll'));
  markWrite($('stopAll'));

  $('loginSubmit').addEventListener('click', submitLogin);
  $('loginPassword').addEventListener('keydown', function (event) {
    if (event.key === 'Enter') { submitLogin(); }
  });
  $('loginButton').addEventListener('click', function () { showLogin(); });
  $('logoutButton').addEventListener('click', logout);

  $('loadRecorderSettings').addEventListener('click', loadRecorderSettings);
  markWrite($('applyRecorderSettings'), 'admin').addEventListener('click', applyRecorderSettings);
  $('loadAppSettings').addEventListener('click', loadAppSettings);
  $('loadVariables').addEventListener('click', loadVariables);
  markWrite($('putVariable')).addEventListener('click', putVariable);
  markWrite($('variablePersist'));
  markWrite($('variableKey'));
  markWrite($('variableValue'));
  $('loadRecordings').addEventListener('click', loadRecordings);

  $('stopPreview').addEventListener('click', function () { stopPreview('stopped'); });
  // The element outlives every connection, so this is attached once. A decode
  // failure is otherwise silent: the picture stops and the network keeps running.
  $('previewPlayer').addEventListener('error', function () {
    var media = $('previewPlayer').error;
    if (previewOnFailure !== null) {
      previewOnFailure('playback error' + (media ? ' (code ' + media.code + ')' : ''));
    }
  });
  // Leaving the page has to release the subscription: the seat is only returned
  // when the response ends, and a background tab would hold it indefinitely.
  window.addEventListener('pagehide', function () { stopPreview(); });

  start();
})();
