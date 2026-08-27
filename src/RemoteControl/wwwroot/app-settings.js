// ProcessRecorderApp remote control UI -- the settings page.
//
// Four forms, all generated from what the server describes: the recorder settings,
// the source (element template -> SrcPipeline, Admin only), the application
// settings and the variables. Nothing here knows a property name by heart -- the
// server's description is the whole truth about what may be edited.
'use strict';

(function () {
  var PRA = window.PRA;
  var core = PRA.core;
  var state = core.state;
  var $ = core.$;
  var text = core.text;
  var status = core.status;
  var cell = core.cell;
  var getJson = core.getJson;
  var send = core.send;
  var markWrite = core.markWrite;
  var writeButton = core.writeButton;

  function syncRecorderSelect() {
    var select = $('recorderSelect');
    var previous = select.value;
    select.replaceChildren();
    state.recorderNames.forEach(function (name) {
      var option = document.createElement('option');
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    });
    if (state.recorderNames.indexOf(previous) >= 0) { select.value = previous; }
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

        var apply = writeButton('Apply', function () {
          patchAppSetting(key, input.value);
        }, 'admin');
        apply.className = 'btn-primary';
        field.appendChild(apply);

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

  PRA.settings = {
    syncRecorderSelect: syncRecorderSelect,
    loadRecorderSettings: loadRecorderSettings,
    applyRecorderSettings: applyRecorderSettings,
    loadSources: loadSources,
    buildSourceForm: buildSourceForm,
    applySource: applySource,
    loadAppSettings: loadAppSettings,
    loadVariables: loadVariables,
    putVariable: putVariable
  };
})();
