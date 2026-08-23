// ProcessRecorderApp remote control UI. No framework, no third-party script.
//
// The access token is never held here: opening the page once with `?token=<token>`
// makes the server set a session cookie, and every write request just says
// `credentials: 'same-origin'`. A 401 therefore means "this browser has no session",
// which is a state of the page, not a value this file can repair.
'use strict';

(function () {
  var writeAllowed = true;

  function $(id) { return document.getElementById(id); }

  function text(node, value) { node.textContent = value === null || value === undefined ? '' : String(value); }

  function status(node, message, isError) {
    text(node, message);
    node.className = isError ? 'status error' : 'status';
  }

  // ---- requests ----

  function getJson(url) {
    return fetch(url, { credentials: 'same-origin' }).then(function (response) {
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
      if (response.status === 401) { denyWrites(); }
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

  // The readable half of the page keeps working without a session; only the
  // controls that write are switched off, so the failure is visible once
  // instead of once per click.
  function denyWrites() {
    writeAllowed = false;
    var notice = $('authNotice');
    text(notice, 'Not authenticated. Open this page with ?token=<access token> once; the session cookie carries it afterwards.');
    notice.className = 'notice';
    var controls = document.querySelectorAll('button, input, select');
    for (var i = 0; i < controls.length; i++) {
      if (controls[i].dataset.write === '1') { controls[i].disabled = true; }
    }
  }

  function writeButton(label, onClick) {
    var button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.dataset.write = '1';
    button.disabled = !writeAllowed;
    button.addEventListener('click', onClick);
    return button;
  }

  function markWrite(node) {
    node.dataset.write = '1';
    if (!writeAllowed) { node.disabled = true; }
    return node;
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
      row.appendChild(actions);

      body.appendChild(row);
    });

    $('startAll').disabled = !writeAllowed || !snapshot.canStartAll;
    $('stopAll').disabled = !writeAllowed || !snapshot.canStopAll;

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
    markWrite(input);
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

        var input = markWrite(document.createElement('input'));
        input.type = 'text';
        input.value = settings[key] === null || settings[key] === undefined ? '' : String(settings[key]);
        field.appendChild(input);

        field.appendChild(writeButton('Apply', function () {
          patchAppSetting(key, input.value);
        }));

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

  // ---- live state ----

  // The server already debounces the state events (200ms), so the handler redraws
  // directly. Reconnection is the browser's job for EventSource.
  function subscribe() {
    var source = new EventSource('/api/events');
    source.addEventListener('state', function (event) {
      renderRecorders(JSON.parse(event.data));
    });
  }

  // ---- startup ----

  $('startAll').addEventListener('click', function () { control('/api/recorders/start-all'); });
  $('stopAll').addEventListener('click', function () { control('/api/recorders/stop-all'); });
  markWrite($('startAll'));
  markWrite($('stopAll'));

  $('loadRecorderSettings').addEventListener('click', loadRecorderSettings);
  markWrite($('applyRecorderSettings')).addEventListener('click', applyRecorderSettings);
  $('loadAppSettings').addEventListener('click', loadAppSettings);
  $('loadVariables').addEventListener('click', loadVariables);
  markWrite($('putVariable')).addEventListener('click', putVariable);
  markWrite($('variablePersist'));
  markWrite($('variableKey'));
  markWrite($('variableValue'));
  $('loadRecordings').addEventListener('click', loadRecordings);

  subscribe();
  loadAppSettings();
  loadVariables();
  loadRecordings();
})();
