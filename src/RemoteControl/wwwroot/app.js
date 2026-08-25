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
  // Whether reading is allowed without signing in. Only `/api/me` knows, so it
  // starts at false: with guest reading off that call answers 401, and starting at
  // true would put a "Cancel" button on the sign-in form that leads nowhere.
  var guest = false;
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
    // The source form is hidden as a whole rather than disabled: it is not an
    // "edit this value" control but a template builder, and half of it greyed out
    // says nothing useful to a Viewer.
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
      preview.addEventListener('click', function () { startSelectedPreview(recorder.name); });
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
      // Shown, never parsed back: the pipeline string is the server's to build.
      text($('sourceCurrent'), 'current: ' + (settings.values.SrcPipeline || ''));
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
    // `remoteEditable` comes from the server (RemoteApiRules.RemoteDeniedRecorderSettings).
    // The names are deliberately not copied into this file: a key added to the deny
    // list would otherwise keep looking editable here until someone remembered.
    // Not routed through `markWrite`, because `permit` would re-enable it for an Admin.
    if (property.remoteEditable === false) {
      input.disabled = true;
      input.title = (property.description ? property.description + ' - ' : '')
        + 'read-only over the remote API';
    } else {
      markWrite(input, 'admin');
    }
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
      // The server refuses these keys outright, so sending them would turn every
      // apply into a 400 that discards the rest of the form.
      if (entry.property.remoteEditable === false) { return; }
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

  // ---- source (template -> SrcPipeline, Admin only) ----
  //
  // The browser never sends a pipeline string. It sends the element name plus the
  // values for the properties and caps fields the server offered, and the server
  // assembles the string -- that is the whole reason `SrcPipeline` may be written
  // here at all while `PATCH .../settings` still refuses it.

  var sourceDefs = [];
  var sourceFields = [];

  function loadSources() {
    getJson('/api/sources').then(function (result) {
      sourceDefs = result.sources;
      var select = $('sourceSelect');
      select.replaceChildren();
      sourceDefs.forEach(function (def) {
        var option = document.createElement('option');
        option.value = def.element;
        option.textContent = def.displayName + ' (' + def.element + ')';
        select.appendChild(option);
      });
      buildSourceForm();
    }).catch(function (error) {
      status($('sourceStatus'), error.message, true);
    });
  }

  function selectedSource() {
    var element = $('sourceSelect').value;
    for (var i = 0; i < sourceDefs.length; i++) {
      if (sourceDefs[i].element === element) { return sourceDefs[i]; }
    }
    return null;
  }

  function buildSourceForm() {
    var host = $('sourceForm');
    host.replaceChildren();
    sourceFields = [];

    var def = selectedSource();
    if (def === null) { return; }

    status($('sourceStatus'), 'recording type: ' + def.recordingType, false);

    def.properties.forEach(function (property) {
      host.appendChild(sourceField(property.name, property, 'properties'));
    });
    def.capsFields.forEach(function (capsField) {
      host.appendChild(sourceField('caps ' + capsField.name, capsField, 'caps'));
    });
  }

  // One row per property or caps field. Every value is a string on the wire, and an
  // empty one is left out of the request: "not set" is how the server is told to
  // leave that property off the pipeline entirely.
  function sourceField(label, definition, group) {
    var field = document.createElement('div');
    field.className = 'field';

    var caption = document.createElement('label');
    text(caption, label);
    if (definition.description) { caption.title = definition.description; }
    field.appendChild(caption);

    var input;
    var choices = definition.kind === 'Bool' ? ['true', 'false'] : definition.choices;
    if (choices && choices.length > 0) {
      input = document.createElement('select');
      appendOption(input, '');
      choices.forEach(function (choice) { appendOption(input, choice); });
    } else {
      input = document.createElement('input');
      input.type = 'text';
    }
    input.value = definition.defaultValue === null || definition.defaultValue === undefined
      ? '' : String(definition.defaultValue);
    if (definition.description) { input.title = definition.description; }
    field.appendChild(input);

    if (definition.conditionallyAvailable) {
      var note = document.createElement('span');
      note.className = 'status';
      text(note, 'not on every build');
      field.appendChild(note);
    }

    sourceFields.push({ name: definition.name, group: group, input: input });
    return field;
  }

  function appendOption(select, value) {
    var option = document.createElement('option');
    option.value = value;
    option.textContent = value;
    select.appendChild(option);
  }

  function applySource() {
    var id = $('recorderSelect').value;
    var def = selectedSource();
    if (!id || def === null) {
      status($('sourceStatus'), 'select a recorder and a source', true);
      return;
    }

    var body = { element: def.element, properties: {}, caps: {} };
    sourceFields.forEach(function (entry) {
      if (entry.input.value === '') { return; }
      body[entry.group][entry.name] = entry.input.value;
    });

    send('PUT', '/api/recorders/' + encodeURIComponent(id) + '/source', body).then(function (result) {
      status($('sourceStatus'), 'applied: ' + result.srcPipeline, false);
      text($('sourceCurrent'), 'current: ' + result.srcPipeline);
    }).catch(function (error) {
      status($('sourceStatus'), error.message, true);
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
        cell(row, (file.inProgress ? 'recording' : '') + (file.fragmented ? ' fragmented' : ''));

        var actions = document.createElement('td');

        var play = document.createElement('button');
        play.type = 'button';
        play.textContent = 'Play';
        // A fragmented recording can be played while it is still being written:
        // its header is complete from the first byte. Anything else has no finished
        // moov box until the recording stops, so only the bytes are offered.
        play.disabled = file.inProgress && !file.fragmented;
        play.addEventListener('click', function () {
          if (file.fragmented) { startFollow(url, file.path); return; }
          stopFollow('');
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

  // ---- follow-along playback of a recording (fragmented MP4 over MSE) ----
  //
  // A fragmented recording writes `ftyp` + `moov` once and never rewrites them, so
  // the file is readable from the first byte -- while it is being recorded and
  // after a forced shutdown. The price is that the duration in `moov` stays 0:
  // pointing `<video src>` at such a file shows a one-second clip whether or not
  // the recording has finished. So the bytes are fed to a MediaSource by hand, and
  // that is the only path used for `fragmented` rows.
  //
  // `mode = 'segments'` -- not the preview's 'sequence'. This is a file with a
  // timeline of its own, and the position the user seeks to has to be the position
  // that plays.
  //
  // Growth is followed with ordinary range requests (`Range: bytes=<next>-`), and
  // `next` advances by the number of bytes that actually arrived. Appends may fall
  // on any boundary. A 416 is not an error here: it means "no longer than last
  // time". `X-In-Progress: false` together with a read that reached the end is what
  // ends the stream.

  // Used when the server does not report `X-Codecs` (an older file, or a header it
  // could not read). The same constant the live preview uses.
  var FOLLOW_CODECS_FALLBACK = 'avc1.4d401f';

  // How much of the past to keep, and the span that triggers a trim. The gap
  // between the two is load-bearing for the same reason as in the preview: without
  // it every `updateend` would call `remove()`, whose own `updateend` would call it
  // again, and `appendBuffer` would never get a turn.
  var FOLLOW_WINDOW_SECONDS = 60;
  var FOLLOW_TRIM_TRIGGER_SECONDS = 70;

  // How long to wait after a 416 before asking again (the file has not grown yet).
  var FOLLOW_POLL_MS = 1000;

  // Where following starts: this far behind the buffered end. Right at the end the
  // decoder runs out on every fragment boundary. This is used once, when the first
  // playable moment arrives -- a follow that started at the beginning of what is
  // buffered would play a minute-old picture and never catch up.
  var FOLLOW_LAG_SECONDS = 1;

  // After that, ordinary playback keeps the position and a correction is made only
  // when it has fallen further behind the buffered end than the trigger; the
  // correction lands this far behind it.
  //
  // **The gap between the two is what removes the stutter.** Correcting toward a
  // fixed distance on every `updateend` means one seek per fragment (one a second),
  // and a decoder that is reset that often never plays smoothly.
  var FOLLOW_CATCHUP_TRIGGER_SECONDS = 3;
  var FOLLOW_CATCHUP_LAG_SECONDS = 1.5;

  // Bumped by every start and every stop: callbacks belonging to an older playback
  // (an in-flight fetch, a pending timer, an `updateend`) return without touching
  // anything.
  var followGeneration = 0;
  var followAbort = null;
  var followTimer = null;
  var followUrl = null;

  // Following the live edge stops for good once the user seeks. The corrections
  // this file makes itself must not count as that, so the position each one asks
  // for is remembered: a `seeking` that lands near it is ours, anything else is
  // the user's. A plain boolean cannot tell the two apart -- a seek the user makes
  // while a correction is pending would consume the flag and be taken for the
  // correction.
  //
  // **Near, not equal.** `seeking` is delivered as a task, and a seek that has
  // already completed by then leaves the element a little past the position that
  // was asked for (measured: 1.599999 asked, 1.60025 once seeked, and more while
  // the machine is loaded). Comparing for equality drops the following at the very
  // first correction, which is the whole of the live edge being followed.
  var FOLLOW_SEEK_MATCH_SECONDS = 0.5;

  var followLive = true;
  var followSeekTarget = null;

  // The active follow's failure hook. The <video> element outlives every playback,
  // so its `error` listener is attached once and routed through here (the same
  // shape as the preview's).
  var followOnFailure = null;

  function releaseFollowUrl() {
    if (followUrl !== null) {
      URL.revokeObjectURL(followUrl);
      followUrl = null;
    }
  }

  // Everything the <video> element holds on to: the element itself keeps decoding
  // and the object URL keeps the MediaSource alive until both are let go.
  function releaseFollowPlayer() {
    followOnFailure = null;
    var video = $('player');
    video.pause();
    video.removeAttribute('src');
    video.load();
    releaseFollowUrl();
  }

  function stopFollow(message) {
    followGeneration++;
    if (followTimer !== null) {
      clearTimeout(followTimer);
      followTimer = null;
    }
    if (followAbort !== null) {
      followAbort.abort();
      followAbort = null;
    }

    releaseFollowPlayer();

    status($('playerStatus'), message === undefined ? '' : message, false);
  }

  function startFollow(url, label) {
    stopFollow('');
    if (typeof MediaSource === 'undefined') {
      status($('playerStatus'), 'this browser has no MediaSource', true);
      return;
    }
    status($('playerStatus'), 'opening ' + label + '...', false);
    openFollow(url, followGeneration);
  }

  // The first response does double duty: it carries the codecs string (needed
  // before `addSourceBuffer`, and only the server can read it out of `avcC`) and
  // the first bytes. So the request comes first and the MediaSource is built from
  // its answer.
  function openFollow(url, generation) {
    var controller = new AbortController();
    followAbort = controller;

    fetch(url, { signal: controller.signal, credentials: 'same-origin', cache: 'no-store' })
      .then(function (response) {
        if (generation !== followGeneration) { return; }
        if (response.status === 401) { showLogin('Sign in to continue.'); return; }
        if (!response.ok) { throw new Error('HTTP ' + response.status); }

        var codecs = response.headers.get('X-Codecs') || FOLLOW_CODECS_FALLBACK;
        var video = $('player');
        var source = new MediaSource();

        releaseFollowUrl();
        followUrl = URL.createObjectURL(source);
        followLive = true;
        followSeekTarget = null;
        video.src = followUrl;

        source.addEventListener('sourceopen', function () {
          if (generation !== followGeneration) { return; }

          var buffer;
          try {
            buffer = source.addSourceBuffer('video/mp4; codecs="' + codecs + '"');
          } catch (error) {
            status($('playerStatus'), error.message, true);
            return;
          }
          buffer.mode = 'segments';
          followFile(url, generation, source, buffer, response);
        });
      })
      .catch(function (error) {
        if (generation !== followGeneration || error.name === 'AbortError') { return; }
        status($('playerStatus'), error.message, true);
      });
  }

  // Owns everything that spans the requests: how far the file has been read, what
  // the last response said about it, and the chunks waiting for the SourceBuffer.
  function followFile(url, generation, source, buffer, first) {
    var video = $('player');
    var queue = [];
    var next = 0;
    var total = null;
    var inProgress = true;
    var reading = false;
    var receivedBytes = false;
    var started = false;
    var trimmed = false;
    // Whether the one unconditional jump to the live edge has been made.
    var joined = false;

    function fail(reason) {
      if (generation !== followGeneration) { return; }
      // Bumping the generation is what stops the rest: a SourceBuffer that has
      // rejected one append never takes the next one, so there is nothing to
      // salvage in place. The element and the object URL are let go for the same
      // reason a stop does it -- what stays attached keeps a dead MediaSource
      // alive and goes on reporting errors from it.
      followGeneration++;
      if (followAbort !== null) { followAbort.abort(); followAbort = null; }
      releaseFollowPlayer();
      status($('playerStatus'), reason, true);
    }

    followOnFailure = fail;

    // One place decides what happens next, and it only runs when the SourceBuffer
    // is idle, the queue is empty and the body has been read to its end.
    function settle() {
      if (!inProgress && (total === null || total <= next)) {
        try {
          if (source.readyState === 'open') { source.endOfStream(); }
        } catch (error) {
          /* the element already has everything; nothing is left to repair */
        }
        status($('playerStatus'), 'complete (' + formatSize(next) + ')', false);
        return;
      }

      // Something arrived, so more may already be there; a 416 means it is not.
      schedule(receivedBytes ? 0 : FOLLOW_POLL_MS);
    }

    function schedule(delay) {
      if (followTimer !== null) { return; }
      followTimer = setTimeout(function () {
        followTimer = null;
        if (generation !== followGeneration) { return; }
        request();
      }, delay);
    }

    // A retry of an append the SourceBuffer refused. It shares the one timer with
    // `schedule`, which is safe because the two cannot both be wanted: a refused
    // append means the queue is not empty, and a request is only ever scheduled
    // from `settle`, which needs it empty.
    function scheduleFlush() {
      if (followTimer !== null) { return; }
      followTimer = setTimeout(function () {
        followTimer = null;
        if (generation !== followGeneration) { return; }
        flush();
      }, FOLLOW_POLL_MS);
    }

    function flush() {
      if (generation !== followGeneration || buffer.updating) { return; }

      // The trim runs before the queue, and one remove() is allowed between two
      // appends. A response that carries the whole file keeps `reading` true and
      // the queue busy from its first chunk to its last, so a trim placed after
      // them would never get a turn on that path; the flag is what keeps it from
      // taking every turn instead (remove() is granular to random access points,
      // so a cut can free nothing and ask to be repeated forever).
      if (!trimmed) {
        trimmed = true;
        if (trimFollow(buffer, false)) { return; }  // its own updateend comes back here
      }

      if (0 < queue.length) {
        // Peek, append, then drop: shifting first loses the chunk when
        // appendBuffer throws, and a byte stream with a hole never recovers.
        var chunk = queue[0];
        try {
          buffer.appendBuffer(chunk);
        } catch (error) {
          // A full SourceBuffer is not a failure: what is already in it still
          // plays, and the media behind the playback position can be freed as the
          // position advances. The chunk stays at the head of the queue. Any other
          // error is fatal, as before.
          if (error.name === 'QuotaExceededError') {
            trimmed = false;
            if (!trimFollow(buffer, true)) { scheduleFlush(); }
            return;
          }
          fail('append failed: ' + error.message);
          return;
        }
        queue.shift();
        trimmed = false;
        if (!started) {
          started = true;
          video.play().catch(function () { /* the browser decides; the controls remain */ });
        }
        return;
      }

      if (reading) { return; }
      settle();
    }

    function read(response) {
      reading = true;
      receivedBytes = false;
      var reader = response.body.getReader();

      function step() {
        return reader.read().then(function (chunk) {
          if (generation !== followGeneration) { return undefined; }
          if (chunk.done) {
            reading = false;
            flush();
            return undefined;
          }

          next += chunk.value.byteLength;
          receivedBytes = true;
          queue.push(chunk.value);
          flush();
          return step();
        });
      }

      return step();
    }

    function request() {
      var controller = new AbortController();
      followAbort = controller;

      fetch(url, {
        signal: controller.signal,
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { 'Range': 'bytes=' + next + '-' }
      }).then(function (response) {
        if (generation !== followGeneration) { return undefined; }
        if (response.status === 401) { showLogin('Sign in to continue.'); return undefined; }

        inProgress = response.headers.get('X-In-Progress') !== 'false';

        // 416 = the file is no longer than what has already been read. The headers
        // still say whether it is going to grow, so this is where a finished
        // recording is recognised.
        if (response.status === 416) {
          receivedBytes = false;
          settle();
          return undefined;
        }

        if (!response.ok) { throw new Error('HTTP ' + response.status); }

        total = totalOf(response, next);
        return read(response);
      }).catch(function (error) {
        if (generation !== followGeneration || error.name === 'AbortError') { return; }
        fail(error.message);
      });
    }

    buffer.addEventListener('error', function () { fail('the source buffer failed'); });

    buffer.addEventListener('updateend', function () {
      if (generation !== followGeneration) { return; }
      followEdge();
      flush();
    });

    // Keep playback near the live edge until the user seeks: after that the
    // position is theirs. A file that is no longer being written has no live edge
    // to follow -- doing it there would put playback one second before the end of
    // the recording and finish it at once.
    //
    // **Following is one jump plus rare corrections, not a position that is
    // rewritten every time.** This runs on every `updateend`, which is once per
    // fragment; assigning `currentTime` that often seeks the element once a
    // second, and every seek resets the decoder.
    function followEdge() {
      if (!followLive || !inProgress) { return; }

      // Nothing is corrected before the element has a position to correct. A seek
      // asked for while the first frames are still being decoded is a seek the
      // start of playback has to wait for, and `play()` has not even been called
      // yet on the append that carries the first frame.
      if (video.readyState < 2 /* HAVE_CURRENT_DATA */) { return; }

      var ranges = video.buffered;
      if (ranges.length === 0) { return; }
      var end = ranges.end(ranges.length - 1);

      if (!joined) {
        joined = true;
        seekFollow(end - FOLLOW_LAG_SECONDS);
        return;
      }

      if (FOLLOW_CATCHUP_TRIGGER_SECONDS < end - video.currentTime) {
        seekFollow(end - FOLLOW_CATCHUP_LAG_SECONDS);
      }
    }

    // Never seek backwards here: the correction exists to close a gap, and asking
    // for a position behind the one that is playing would drop what has already
    // been decoded. The position asked for is recorded so that the `seeking`
    // listener does not read it as the user taking over.
    function seekFollow(target) {
      if (target <= video.currentTime) { return; }
      followSeekTarget = target;
      video.currentTime = target;
    }

    inProgress = first.headers.get('X-In-Progress') !== 'false';
    total = totalOf(first, 0);
    // The first body is read outside `request()`, so it needs the same catch:
    // a connection lost while it is being read otherwise rejects into nothing and
    // the player stops without a word.
    read(first).catch(function (error) {
      if (generation !== followGeneration || error.name === 'AbortError') { return; }
      fail(error.message);
    });
  }

  // Never cut past where playback is: the user may have seeked back into the part
  // that would otherwise be dropped. `force` frees everything behind the playback
  // position no matter how short the buffered span is -- the caller has been told
  // the SourceBuffer is full, so anything freed is better than nothing.
  function trimFollow(buffer, force) {
    var video = $('player');
    var ranges = video.buffered;
    if (ranges.length === 0) { return false; }

    var start = ranges.start(0);
    var end = ranges.end(ranges.length - 1);

    // Compare the span, not the end: `end` passes 70 once and stays past it.
    if (!force && end - start <= FOLLOW_TRIM_TRIGGER_SECONDS) { return false; }

    var cut = force ? video.currentTime : Math.min(end - FOLLOW_WINDOW_SECONDS, video.currentTime);
    if (cut <= start) { return false; }

    try {
      buffer.remove(start, cut);
      return true;
    } catch (error) {
      return false;  // the next updateend tries again
    }
  }

  // The length of the whole file as the server saw it when it opened the file:
  // `Content-Range: bytes <from>-<to>/<total>`, or the plain length of a 200.
  function totalOf(response, from) {
    var range = response.headers.get('Content-Range');
    if (range !== null) {
      var slash = range.lastIndexOf('/');
      var parsed = slash < 0 ? NaN : parseInt(range.substring(slash + 1), 10);
      if (!isNaN(parsed)) { return parsed; }
    }
    var length = parseInt(response.headers.get('Content-Length'), 10);
    return isNaN(length) ? null : from + length;
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

  // The recorder the running preview belongs to, so that switching the mode selector
  // can reopen the same one. Cleared by stopPreview, set by whichever mode started.
  var previewTarget = null;

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

  // Both modes are torn down here, because both hang their state on the same four
  // handles: the generation counter, the fetch controller, the pending timer (the
  // chunked reconnect or the DASH manifest poll) and the object URL.
  function stopPreview(message) {
    previewGeneration++;
    previewTarget = null;
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

  // The row's "Preview" button opens whichever stream the selector names. The two
  // modes cannot share a MediaSource (different init, different timeline), so
  // switching modes is expressed as "stop, then start the same recorder again".
  function startSelectedPreview(id) {
    if ($('previewMode').value === 'dash') { startDashPreview(id); } else { startPreview(id); }
  }

  function startPreview(id) {
    stopPreview('');
    if (!previewSupported()) {
      status($('previewStatus'), 'this browser cannot play ' + PREVIEW_MIME, true);
      return;
    }
    previewTarget = id;
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
        // A 401 is not about the stream: this browser has no session any more, and
        // the only repair is the sign-in form (which stops the preview as well).
        if (response.status === 401) { showLogin('Sign in to continue.'); }
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

  // ---- DASH preview (the recorder's own preview settings) ----

  // The second mode. The server re-encodes at the recorder's preview resolution,
  // frame rate and bitrate, and publishes the result as a live DASH presentation:
  // a manifest that lists the segments it still holds, one init segment, and the
  // segments themselves. There is no DASH library here -- what a player has to do
  // for a single-representation, single-period live stream is: read the timeline,
  // fetch what is new, append it to a SourceBuffer.
  //
  // The three things that differ from the chunked mode, and why:
  //
  // 1. `mode = 'segments'`, not 'sequence'. The segments carry their own decode
  //    times and the manifest indexes them by those times, so they must land where
  //    they say they do. `timestampOffset` moves that timeline to zero, and it is
  //    set *before* the init is appended -- afterwards it no longer applies to what
  //    the buffer already holds.
  // 2. Fetching the manifest is the subscription. The server keeps the encoder alive
  //    only while somebody is reading, so the poll below is what "watching" means;
  //    stopping the poll is what releases it (nothing else is sent).
  // 3. `Period@id` changing means the server rebuilt the continuum (settings changed,
  //    the lease expired and it came back, the source renegotiated). The old init
  //    cannot decode the new segments and a second init in the same SourceBuffer is
  //    exactly what breaks MSE, so that case rebuilds everything from scratch.

  var DASH_POLL_MS = 1000;

  // The server's word for "the encoder is running but nothing is ready yet". It
  // arrives as the `error` of a 503 and any of the three requests can meet it, so
  // none of them treats it as an answer (see `failUnlessStarting`); every other
  // non-OK answer stops the preview.
  // The C# side owns this string (Components.DashPreviewReasons.Starting) and an L1
  // test keeps the two copies equal.
  var DASH_STARTING_ERROR = 'dash preview is starting';

  var DASH_STARTING_STATUS = 'DASH: starting…';

  function dashUrl(id, file) {
    return '/api/recorders/' + encodeURIComponent(id) + '/dash/' + file;
  }

  function startDashPreview(id) {
    stopPreview('');
    if (!previewSupported()) {
      status($('previewStatus'), 'this browser cannot play ' + PREVIEW_MIME, true);
      return;
    }
    previewTarget = id;
    status($('previewStatus'), DASH_STARTING_STATUS, false);
    openDashPreview(id, previewGeneration);
  }

  function openDashPreview(id, generation) {
    var video = $('previewPlayer');
    var controller = new AbortController();
    previewAbort = controller;

    var buffer = null;
    var period = null;
    var taken = new Set();
    var wanted = [];
    var queue = [];
    var queued = 0;
    var appended = 0;
    var fetching = false;
    var broken = false;

    // The init has to be in the SourceBuffer before any media reaches it, and the
    // manifest poll runs on its own clock: without this gate the second poll can
    // append a segment while the init fetch is still in flight, which is the one
    // mistake MSE answers with a dead SourceBuffer. `initFetching` keeps the retry
    // (see `take`) from asking for the same init twice.
    var initReady = false;
    var initFetching = false;

    // Attributes of the continuum, fixed once the first manifest has been read.
    var initFile = '';
    var mediaTemplate = '';

    function alive() { return !broken && generation === previewGeneration; }

    // One place decides that this presentation is unusable. Unlike the chunked mode
    // there is no reconnect: every way this fails is an answer, not a hiccup.
    function fail(reason) {
      if (!alive()) { return; }
      broken = true;
      previewOnFailure = null;
      controller.abort();
      if (previewTimer !== null) { clearTimeout(previewTimer); previewTimer = null; }
      status($('previewStatus'), reason, true);
    }

    previewOnFailure = fail;

    // Every non-OK answer except one is final. The exception is the 503 that carries
    // `DASH_STARTING_ERROR`: the server empties the ring whenever it rebuilds the
    // continuum (settings changed, caps changed, the encoder was dropped), so the
    // manifest, the init and a segment can all answer it. Waiting is the only repair
    // and it has no deadline here -- the operator decides when to stop looking. The
    // fetch that ran into it is dropped and the manifest poll carries on: the next
    // manifest either comes back on the same `Period@id` or on a new one, and a new
    // one rebuilds the presentation from scratch.
    function failUnlessStarting(response) {
      if (response.status === 401) { showLogin('Sign in to continue.'); return undefined; }
      return response.json().catch(function () { return {}; }).then(function (body) {
        if (response.status === 503 && body.error === DASH_STARTING_ERROR) {
          status($('previewStatus'), DASH_STARTING_STATUS, false);
          return;
        }
        fail(describe(body, response.status));
      });
    }

    // Peek, append, then drop: shifting first loses the chunk when appendBuffer
    // throws, and a byte stream with a hole in it never recovers.
    function flush() {
      if (!alive() || buffer === null || buffer.updating || queue.length === 0) { return; }

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

    function enqueue(bytes) {
      queue.push(bytes);
      queued += bytes.byteLength;
      if (queued > PREVIEW_MAX_QUEUE_BYTES) {
        fail('the player fell too far behind (' + formatSize(queued) + ' unread)');
        return;
      }
      flush();
    }

    function schedule() {
      if (!alive() || previewTimer !== null) { return; }
      previewTimer = setTimeout(function () {
        previewTimer = null;
        poll();
      }, DASH_POLL_MS);
    }

    function poll() {
      if (!alive()) { return; }

      fetch(dashUrl(id, 'manifest.mpd'), {
        signal: controller.signal,
        credentials: 'same-origin'
      }).then(function (response) {
        if (!alive()) { return undefined; }
        if (response.ok) {
          return response.text().then(function (body) {
            if (alive()) { readManifest(body); }
          });
        }
        return failUnlessStarting(response);
      }).catch(function (error) {
        if (!alive() || error.name === 'AbortError') { return; }
        fail(error.message);
      }).then(function () {
        schedule();
      });
    }

    function readManifest(body) {
      var mpd = new DOMParser().parseFromString(body, 'application/xml');
      if (mpd.getElementsByTagName('parsererror').length !== 0) {
        fail('the manifest is not well-formed XML');
        return;
      }

      var periodNode = mpd.getElementsByTagName('Period')[0];
      var setNode = mpd.getElementsByTagName('AdaptationSet')[0];
      var templateNode = mpd.getElementsByTagName('SegmentTemplate')[0];
      if (!periodNode || !setNode || !templateNode) {
        fail('the manifest has no Period, AdaptationSet or SegmentTemplate');
        return;
      }

      var current = periodNode.getAttribute('id');
      if (period !== null) {
        if (current !== period) { rebuild(); return; }
        take(timelineOf(templateNode));
        return;
      }

      var timescale = Number(templateNode.getAttribute('timescale'));
      var offset = Number(templateNode.getAttribute('presentationTimeOffset'));
      var codecs = setNode.getAttribute('codecs');
      initFile = templateNode.getAttribute('initialization');
      mediaTemplate = templateNode.getAttribute('media');

      if (!(timescale > 0) || !isFinite(offset) || !codecs || !initFile || !mediaTemplate) {
        fail('the manifest is missing the values needed to play it');
        return;
      }

      var mime = 'video/mp4; codecs="' + codecs + '"';
      if (!MediaSource.isTypeSupported(mime)) {
        fail('this browser cannot play ' + mime);
        return;
      }

      period = current;
      begin(mime, offset / timescale, timelineOf(templateNode));
    }

    // The `t` values stay strings: they are what goes into the URL, and a 64-bit
    // decode time is not always exact as a JavaScript number.
    function timelineOf(templateNode) {
      var times = [];
      var entries = templateNode.getElementsByTagName('S');
      for (var i = 0; i < entries.length; i++) {
        var t = entries[i].getAttribute('t');
        if (t !== null) { times.push(t); }
      }
      return times;
    }

    function begin(mime, offsetSeconds, times) {
      var source = new MediaSource();

      releasePreviewUrl();
      previewUrl = URL.createObjectURL(source);
      video.src = previewUrl;

      source.addEventListener('sourceopen', function () {
        if (!alive()) { return; }

        try {
          buffer = source.addSourceBuffer(mime);
        } catch (error) {
          fail(error.message);
          return;
        }

        buffer.mode = 'segments';
        buffer.timestampOffset = -offsetSeconds;

        buffer.addEventListener('error', function () { fail('the source buffer failed'); });
        buffer.addEventListener('updateend', function () {
          if (!alive()) { return; }
          trimPreview(video, buffer);
          followPreview(video);
          flush();
        });

        take(times);
      });
    }

    function fetchInit() {
      if (initReady || initFetching) { return; }
      initFetching = true;

      fetch(dashUrl(id, initFile), {
        signal: controller.signal,
        credentials: 'same-origin'
      }).then(function (response) {
        if (!alive()) { return undefined; }
        if (!response.ok) { return failUnlessStarting(response); }
        return response.arrayBuffer().then(function (bytes) {
          if (!alive()) { return; }
          // The queue is first-in-first-out and `flush` only ever appends its head,
          // so putting the init in first is what makes it reach the SourceBuffer first.
          initReady = true;
          enqueue(new Uint8Array(bytes));
          fetchNext();
        });
      }).catch(function (error) {
        if (alive() && error.name !== 'AbortError') { fail(error.message); }
      }).then(function () {
        initFetching = false;
      });
    }

    function take(times) {
      // `taken` only exists to keep the same `t` from being asked for twice, and the
      // manifest is the whole truth about what is askable: the ring holds a fixed
      // number of segments and the times only ever move forward, so a `t` the
      // manifest no longer lists can never be offered again. Dropping those keeps
      // this Set the size of the ring instead of the length of the session.
      var listed = new Set(times);
      taken.forEach(function (t) {
        if (!listed.has(t)) { taken.delete(t); }
      });

      times.forEach(function (t) {
        if (!taken.has(t)) {
          taken.add(t);
          wanted.push(t);
        }
      });
      wanted.sort(function (a, b) { return Number(a) - Number(b); });

      // Nothing may be fetched ahead of the init. A dropped init fetch (a 503 while
      // the server rebuilds) comes back here on the next manifest, so the poll is
      // also the retry.
      if (!initReady) { fetchInit(); return; }
      fetchNext();
    }

    // One segment in flight at a time. Segments have to be appended in order, and
    // asking for the whole window at once only moves the queueing into the network.
    function fetchNext() {
      if (!alive() || fetching || !initReady || buffer === null || wanted.length === 0) { return; }

      fetching = true;
      var time = wanted.shift();

      fetch(dashUrl(id, mediaTemplate.replace('$Time$', time)), {
        signal: controller.signal,
        credentials: 'same-origin'
      }).then(function (response) {
        if (!alive()) { return undefined; }
        // 404 means the segment left the ring before we got to it. It is already
        // marked as taken, so skipping is the whole repair: the picture jumps.
        if (response.status === 404) { return undefined; }
        if (!response.ok) { return failUnlessStarting(response); }
        return response.arrayBuffer().then(function (bytes) {
          if (!alive()) { return; }
          enqueue(new Uint8Array(bytes));
          appended++;
          status($('previewStatus'), 'DASH: live (' + appended + ')', false);
        });
      }).catch(function (error) {
        if (alive() && error.name !== 'AbortError') { fail(error.message); }
      }).then(function () {
        fetching = false;
        fetchNext();
      });
    }

    function rebuild() {
      stopPreview(DASH_STARTING_STATUS);
      startDashPreview(id);
    }

    poll();
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

  function showLogin(message) {
    stopPreview();
    stopFollow();
    if (events !== null) { events.close(); events = null; }
    $('mainSections').classList.add('hidden');
    $('identity').classList.add('hidden');
    $('loginSection').classList.remove('hidden');
    $('loginPassword').value = '';
    // Only a guest has a screen to go back to: with guest reading off, cancelling
    // would land on a page that answers 401 to everything.
    $('loginCancel').hidden = !guest;
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
    // Deliberately not routed through `getJson`: that would show the form before
    // `guest` is known, and the form draws its "Cancel" button from it. A 401 here
    // is the answer "reading needs a session", which is exactly `guest === false`.
    fetch('/api/me', { credentials: 'same-origin' }).then(function (response) {
      if (response.status === 401) {
        guest = false;
        showLogin('Sign in to continue.');
        return null;
      }
      return response.json().then(function (body) {
        if (!response.ok) { throw new Error(describe(body, response.status)); }
        return body;
      });
    }).then(function (me) {
      if (me === null) { return; }
      role = me.role;
      guest = me.guest;
      userName = me.name;
      applyPermissions();
      showApp();
      subscribe();
      // Only an Admin can use the result, and enumerating monitors and cameras is
      // not free -- so it is not fetched for the roles that cannot apply it.
      if (allows('admin')) { loadSources(); }
      loadAppSettings();
      loadVariables();
      loadRecordings();
    }).catch(function () {
      // The 401 is handled above (it decides `guest`). Everything else lands here
      // and goes to the form too, because nothing on the main page can be trusted
      // to have loaded.
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
  // Back to reading as a guest. Routed through `start()` so the page is rebuilt from
  // `/api/me`: the session may have expired while the form was open.
  $('loginCancel').addEventListener('click', function () { start(); });
  $('logoutButton').addEventListener('click', logout);

  $('loadRecorderSettings').addEventListener('click', loadRecorderSettings);
  // The pipeline on screen belongs to the recorder that was loaded. Leaving it
  // there after the selection changes offers another recorder's value for Apply.
  $('recorderSelect').addEventListener('change', function () {
    text($('sourceCurrent'), '');
  });
  markWrite($('applyRecorderSettings'), 'admin').addEventListener('click', applyRecorderSettings);
  $('sourceSelect').addEventListener('change', buildSourceForm);
  markWrite($('applySource'), 'admin').addEventListener('click', applySource);
  $('loadAppSettings').addEventListener('click', loadAppSettings);
  $('loadVariables').addEventListener('click', loadVariables);
  markWrite($('putVariable')).addEventListener('click', putVariable);
  markWrite($('variablePersist'));
  markWrite($('variableKey'));
  markWrite($('variableValue'));
  $('loadRecordings').addEventListener('click', loadRecordings);
  $('stopPlayer').addEventListener('click', function () { stopFollow('stopped'); });
  // A seek the user made ends the live-edge following. A correction this file made
  // is recognised by the position it asked for -- within FOLLOW_SEEK_MATCH_SECONDS,
  // because the element has usually moved on by the time this runs -- and only
  // that one is forgiven.
  $('player').addEventListener('seeking', function () {
    if (followSeekTarget !== null
        && Math.abs($('player').currentTime - followSeekTarget) < FOLLOW_SEEK_MATCH_SECONDS) {
      followSeekTarget = null;
      return;
    }
    followSeekTarget = null;
    followLive = false;
  });

  // The element outlives every playback, so this is attached once. A decode
  // failure is otherwise silent: the picture stops and the polling keeps running.
  $('player').addEventListener('error', function () {
    var media = $('player').error;
    if (followOnFailure !== null) {
      followOnFailure('playback error' + (media ? ' (code ' + media.code + ')' : ''));
    }
  });

  $('stopPreview').addEventListener('click', function () { stopPreview('stopped'); });
  // Changing the mode while a preview runs reopens the same recorder in the new one.
  // Nothing can be carried over: the two modes differ in codec parameters, in how the
  // timeline is built and in how the server accounts for the viewer.
  $('previewMode').addEventListener('change', function () {
    if (previewTarget !== null) { startSelectedPreview(previewTarget); }
  });
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
  window.addEventListener('pagehide', function () { stopPreview(); stopFollow(); });

  start();
})();
