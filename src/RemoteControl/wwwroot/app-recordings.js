// ProcessRecorderApp remote control UI -- the recordings page.
//
// What is on screen is one day of one recorder: the calendar picks the day, the
// `<select>` picks the recorder, and `#recordingsBody` holds the rows of that
// combination and nothing else. "Nothing here" and "there is more" are said in
// `#recordingsEmpty` / `#recordingsTruncated`, outside the table body.
//
// The two ways a row can be played: a plain `<video src>` for a file whose `moov` is
// finished, and the follow-along MSE path (app-player.js) for a fragmented one.
// Which of the two a row gets is decided here; how each one plays is not this
// file's business.
'use strict';

(function () {
  var PRA = window.PRA;
  var core = PRA.core;
  var player = PRA.player;
  var $ = core.$;
  var text = core.text;
  var status = core.status;
  var cell = core.cell;
  var getJson = core.getJson;
  var formatSize = core.formatSize;

  // The server refuses anything above this, and one day of one recorder is not
  // expected to reach it; `hasMore` is what says the day was cut off.
  var DAY_LIMIT = 1000;

  // A burst of `recording` events (a rotation, a batch start) is one reload, not one
  // per event: the list is fetched once, a second after the last of them.
  var REFRESH_DEBOUNCE_MS = 1000;

  // Written as escapes: these files are ASCII, so a punctuation mark that is not
  // cannot be broken by whatever encoding the response is read with.
  var EM_DASH = '\u2014';
  var MIDDLE_DOT = '\u00B7';

  // 2023-01-01 was a Sunday. The week starts there whatever the locale would say:
  // the grid's first column has to mean the same thing as `Date.getDay() === 0`,
  // which is what places the first of the month.
  var WEEK_ORIGIN = new Date(2023, 0, 1);

  // ---- state ----

  var today = new Date();

  // The month the calendar draws, as `getFullYear()` / `getMonth()` (0-based).
  var month = { y: today.getFullYear(), m: today.getMonth() };

  // The day whose recordings the table holds, 'yyyy-MM-dd' in the browser's own
  // zone. null until the first listing says which day the newest recording is on
  // (and stays null while there are none).
  var selectedDay = null;

  // '' means every recorder. Never sent as a parameter in that case.
  var recorder = '';

  // The recorder names the last drawn listing carried. Kept because the `<select>`
  // is rebuilt from more places than the listing arrives at (the `state` event, the
  // router): rebuilding it from the settings alone would drop the name of a
  // recorder that has been removed there but still has recordings on disk.
  var seenRecorders = [];

  // date -> count, for the month on screen.
  var days = new Map();

  // The row that was last played, so the highlight survives a redraw.
  var selectedPath = '';

  var refreshTimer = null;

  // A `recording` event that arrived while another page was on screen. The fetch is
  // deferred rather than dropped: the answer would be thrown away anyway, and this
  // page can be left alone for a long time.
  var dirty = false;

  // Answers are painted only if nothing newer was asked for in the meantime.
  // Without this, switching recorder or day twice in quick succession can leave the
  // slower of the two answers on screen.
  var generation = 0;

  function begin() { return ++generation; }

  function current(token) { return token === generation; }

  // ---- dates ----

  function pad2(value) { return value < 10 ? '0' + value : String(value); }

  function dayKey(date) {
    return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
  }

  // `getTimezoneOffset()` counts minutes *behind* UTC, so the sign is the other way
  // round from the one written in an ISO-8601 offset.
  function offsetOf(date) {
    var minutes = -date.getTimezoneOffset();
    var absolute = Math.abs(minutes);
    return (minutes < 0 ? '-' : '+') + pad2(Math.floor(absolute / 60)) + ':' + pad2(absolute % 60);
  }

  // Midnight of a local day, written with that day's own offset. Not 'yyyy-MM-dd':
  // the server would read a bare date as UTC, which is a different instant.
  function localIso(y, m, d) {
    var at = new Date(y, m, d, 0, 0, 0, 0);
    return dayKey(at) + 'T00:00:00' + offsetOf(at);
  }

  // The zone `recording-days` counts in. A fixed offset, not a zone id: the browser
  // knows its current offset and nothing about the rules behind it, so a month that
  // crosses a daylight-saving change is counted with one offset throughout.
  function tzParam() { return offsetOf(new Date()); }

  // 'yyyy-MM-dd' -> the local midnight of that day and of the one after it.
  function dayWindow(key) {
    var parts = key.split('-');
    var y = Number(parts[0]);
    var m = Number(parts[1]) - 1;
    var d = Number(parts[2]);
    return { from: localIso(y, m, d), to: localIso(y, m, d + 1) };
  }

  function formatDuration(ms) {
    if (ms === null || ms === undefined) { return EM_DASH; }
    var total = Math.round(ms / 1000);
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor(total / 60) % 60;
    var seconds = total % 60;
    if (hours > 0) { return hours + ':' + pad2(minutes) + ':' + pad2(seconds); }
    return minutes + ':' + pad2(seconds);
  }

  // ---- requests ----

  // Each segment is escaped on its own: the separators are part of the route and
  // must survive, everything else in a filename may not be URL-safe.
  function encodePath(path) {
    return path.split('/').map(encodeURIComponent).join('/');
  }

  function lastSegment(path) {
    var cut = path.lastIndexOf('/');
    return cut < 0 ? path : path.substring(cut + 1);
  }

  // The recorder is left out entirely when every recorder is wanted: an empty value
  // and no value mean the same thing to the server, and not writing it keeps the
  // "all" case out of the query.
  function withRecorder(search) {
    if (recorder !== '') { search.set('recorder', recorder); }
    return search;
  }

  function listingUrl(search) { return '/api/recordings?' + withRecorder(search).toString(); }

  // ---- the recorder filter ----

  // The configured recorders plus whatever the listing actually carried plus the
  // current choice: a recorder can be removed from the settings while its
  // recordings are still on disk, and the selection must not disappear under the
  // list it is filtering.
  function syncRecorderOptions(seen) {
    if (seen !== undefined) { seenRecorders = seen; }

    var names = [];

    function add(name) {
      if (name && names.indexOf(name) < 0) { names.push(name); }
    }

    core.state.recorderNames.forEach(add);
    seenRecorders.forEach(add);
    add(recorder);
    names.sort();

    var select = $('recordingsRecorder');
    select.replaceChildren();

    var all = document.createElement('option');
    all.value = '';
    all.textContent = 'All recorders';
    select.appendChild(all);

    names.forEach(function (name) {
      var option = document.createElement('option');
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    });

    select.value = recorder;
  }

  // ---- the calendar ----

  function renderCalendar() {
    var grid = $('calendarGrid');
    grid.replaceChildren();

    text($('calendarMonth'), new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'long' })
      .format(new Date(month.y, month.m, 1)));

    var weekday = new Intl.DateTimeFormat(undefined, { weekday: 'short' });
    for (var w = 0; w < 7; w++) {
      var head = document.createElement('span');
      head.className = 'calendar-weekday';
      head.setAttribute('role', 'columnheader');
      head.textContent = weekday.format(new Date(
        WEEK_ORIGIN.getFullYear(), WEEK_ORIGIN.getMonth(), WEEK_ORIGIN.getDate() + w));
      grid.appendChild(head);
    }

    // Day 0 of the next month is the last day of this one.
    var length = new Date(month.y, month.m + 1, 0).getDate();
    var lead = new Date(month.y, month.m, 1).getDay();
    var todayKey = dayKey(new Date());

    for (var d = 1; d <= length; d++) {
      var key = localIso(month.y, month.m, d).substring(0, 10);
      var count = days.has(key) ? days.get(key) : 0;

      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'calendar-day';
      button.setAttribute('role', 'gridcell');
      button.dataset.date = key;
      // Only the first cell is placed; the rest follow it across the seven columns.
      if (d === 1) { button.style.gridColumnStart = String(lead + 1); }

      var number = document.createElement('span');
      number.className = 'calendar-number';
      number.textContent = String(d);
      button.appendChild(number);

      if (count > 0) {
        var badge = document.createElement('span');
        badge.className = 'badge';
        badge.textContent = String(count);
        button.appendChild(badge);
      } else {
        // A day with nothing on it is not a choice: picking it could only empty the
        // table, and the calendar already says so by having no badge.
        button.disabled = true;
      }

      if (key === selectedDay) { button.classList.add('selected'); }
      button.setAttribute('aria-pressed', key === selectedDay ? 'true' : 'false');
      if (key === todayKey) { button.classList.add('today'); }

      // The date is read back off the button rather than captured, so the handler is
      // the same function for every cell.
      button.addEventListener('click', function (event) {
        selectDay(event.currentTarget.dataset.date);
      });

      grid.appendChild(button);
    }
  }

  // ---- the table ----

  function thumbnailOf(file) {
    if (!file.hasThumbnail) {
      var empty = document.createElement('span');
      empty.className = 'thumb thumb-empty';
      var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('aria-hidden', 'true');
      var use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
      use.setAttribute('href', '#i-image');
      svg.appendChild(use);
      empty.appendChild(svg);
      return empty;
    }

    var image = document.createElement('img');
    image.className = 'thumb';
    image.loading = 'lazy';
    image.decoding = 'async';
    image.alt = '';
    image.src = '/api/recording-thumbnails/' + encodePath(file.path);
    return image;
  }

  function nameCell(row, file) {
    var td = document.createElement('td');

    var box = document.createElement('div');
    box.className = 'rec-name';
    box.appendChild(thumbnailOf(file));

    var lines = document.createElement('div');

    var name = document.createElement('div');
    name.className = 'rec-file';
    name.textContent = lastSegment(file.path);
    // The row shows the filename; the path it sits at is one hover away.
    name.title = file.path;
    lines.appendChild(name);

    var who = document.createElement('div');
    who.className = 'muted';
    who.textContent = file.recorder;
    lines.appendChild(who);

    box.appendChild(lines);
    td.appendChild(box);
    row.appendChild(td);
  }

  // **Play is the first button of the row and Download follows it.** Nothing before
  // this cell may be a `<button>`: the row is searched in document order for the one
  // that starts a playback.
  function actionsCell(row, file) {
    var url = '/api/recordings/' + encodePath(file.path);
    var td = document.createElement('td');

    var play = document.createElement('button');
    play.type = 'button';
    play.className = 'btn-primary';
    play.textContent = 'Play';
    // A fragmented recording can be played while it is still being written:
    // its header is complete from the first byte. Anything else has no finished
    // moov box until the recording stops, so only the bytes are offered.
    play.disabled = file.inProgress && !file.fragmented;
    play.addEventListener('click', function () {
      selectedPath = file.path;
      markSelectedRow();
      if (file.fragmented) { player.startFollow(url, file.path); return; }
      player.stopFollow('');
      var element = $('player');
      element.src = url;
      element.play().catch(function () { /* the browser decides; the controls remain */ });
    });
    td.appendChild(play);

    var download = document.createElement('a');
    download.href = url + '?download=1';
    download.download = '';
    download.textContent = 'Download';
    td.appendChild(download);

    row.appendChild(td);
  }

  function markSelectedRow() {
    var rows = $('recordingsBody').rows;
    for (var i = 0; i < rows.length; i++) {
      rows[i].classList.toggle('selected', rows[i].dataset.path === selectedPath);
    }
  }

  function renderRows(files) {
    var body = $('recordingsBody');
    body.replaceChildren();

    files.forEach(function (file) {
      var row = document.createElement('tr');
      row.dataset.path = file.path;

      nameCell(row, file);
      cell(row, formatSize(file.length));
      cell(row, new Date(file.startTimeUtc).toLocaleString());
      // **Written exactly as it has always been.** This is the one cell whose text
      // the layers above read to tell a running recording from a finished one.
      cell(row, (file.inProgress ? 'recording' : '') + (file.fragmented ? ' fragmented' : ''));
      cell(row, formatDuration(file.durationMs));
      // The raw value, `uia:` prefix and all: it says both what started the
      // recording and, for a trigger, which one.
      cell(row, file.trigger === null || file.trigger === undefined ? EM_DASH : file.trigger);
      actionsCell(row, file);

      body.appendChild(row);
    });

    markSelectedRow();
  }

  // ---- loading ----

  // The day the newest recording of the current recorder is on. Asked for exactly
  // one item: the point is to find the day, not to draw anything.
  function pickLatestDay(token) {
    var search = new URLSearchParams();
    search.set('limit', '1');

    return getJson(listingUrl(search)).then(function (result) {
      if (!current(token)) { return; }
      status($('recordingsRoot'), result.root, false);

      if (result.files.length === 0) {
        // Nothing to point at, so the calendar shows the month the reader is in
        // rather than whichever one the last recorder left behind.
        var now = new Date();
        month = { y: now.getFullYear(), m: now.getMonth() };
        return;
      }

      var start = new Date(result.files[0].startTimeUtc);
      selectedDay = dayKey(start);
      month = { y: start.getFullYear(), m: start.getMonth() };
    });
  }

  function loadDays(token) {
    var search = new URLSearchParams();
    search.set('from', localIso(month.y, month.m, 1));
    search.set('to', localIso(month.y, month.m + 1, 1));
    search.set('tz', tzParam());

    return getJson('/api/recording-days?' + withRecorder(search).toString()).then(function (result) {
      if (!current(token)) { return; }

      days = new Map();
      result.days.forEach(function (day) { days.set(day.date, day.count); });
      renderCalendar();
    });
  }

  // The day the calendar counted last, when the listing did not name one.
  //
  // **The two answers do not have to come from the same state of the server.** The
  // index is built once, on the first request that reaches it, and a request that
  // arrives while that build is running is answered from the empty snapshot it has
  // so far -- the page's step 1 can therefore come back empty while step 2, one
  // round trip later, counts the very recordings step 1 did not see. Taking the
  // calendar's newest day is what keeps that from leaving an empty table behind.
  function adoptLatestCountedDay() {
    if (selectedDay !== null) { return; }

    var latest = null;
    // 'yyyy-MM-dd' sorts as text exactly as it does as a date.
    days.forEach(function (count, date) {
      if (count > 0 && (latest === null || date > latest)) { latest = date; }
    });
    if (latest === null) { return; }

    selectedDay = latest;
    // The calendar was drawn before this day was picked, so it has no highlight yet.
    renderCalendar();
  }

  function loadDay(token) {
    if (selectedDay === null) {
      renderRows([]);
      text($('recordingsDay'), '');
      $('recordingsEmpty').hidden = false;
      $('recordingsTruncated').hidden = true;
      return Promise.resolve();
    }

    var bounds = dayWindow(selectedDay);
    var search = new URLSearchParams();
    search.set('from', bounds.from);
    search.set('to', bounds.to);
    search.set('limit', String(DAY_LIMIT));

    return getJson(listingUrl(search)).then(function (result) {
      if (!current(token)) { return; }

      status($('recordingsRoot'), result.root, false);
      renderRows(result.files);
      syncRecorderOptions(result.files.map(function (file) { return file.recorder; }));

      var label = selectedDay + ' ' + MIDDLE_DOT + ' ' + result.total + ' recordings';
      // A playback of a recording that is not in the list any more (another day, a
      // different recorder, deleted) keeps running: only the highlight is gone.
      if (selectedPath !== '' && !result.files.some(function (file) { return file.path === selectedPath; })) {
        label += ' ' + MIDDLE_DOT + ' playing ' + lastSegment(selectedPath);
      }
      text($('recordingsDay'), label);

      $('recordingsEmpty').hidden = result.files.length > 0;
      $('recordingsTruncated').hidden = !result.hasMore;
    });
  }

  function report(error) { status($('recordingsRoot'), error.message, true); }

  var loading = false;
  var reloadPending = false;

  // Every path into the page comes through here: the Refresh button, the first read
  // after signing in, and the `recording` events.
  //
  // **A reload that arrives while one is running is remembered, not started.** A
  // full reload is up to three requests in a row, and each one begins by declaring
  // itself the newest (which is what stops a slow answer from painting over a newer
  // one). Starting a second reload on top of a running one therefore abandons the
  // first, so a caller that asks again faster than the round trips take -- a page
  // polling for a change -- would abandon every attempt and never paint anything.
  function loadRecordings() {
    if (loading) { reloadPending = true; return; }
    loading = true;

    var token = begin();
    syncRecorderOptions();

    var first = selectedDay === null ? pickLatestDay(token) : Promise.resolve();

    // Both arms of the last `then`, so that `loading` is cleared even if reporting
    // the failure fails in turn. A flag left set here would wedge the page for
    // good: every later reload would see it and only set `reloadPending`.
    function settle() {
      loading = false;
      if (!reloadPending) { return; }
      reloadPending = false;
      loadRecordings();
    }

    first
      .then(function () { return current(token) ? loadDays(token) : undefined; })
      .then(function () {
        if (!current(token)) { return undefined; }
        adoptLatestCountedDay();
        return loadDay(token);
      })
      // The rows already on screen are left alone: an answer that never came is not
      // the same news as "there is nothing here".
      .catch(report)
      .then(settle, settle);
  }

  function selectDay(date) {
    selectedDay = date;
    renderCalendar();
    loadDay(begin()).catch(report);
  }

  // The selected day is kept when the month changes: it is still the day the table
  // holds, it simply has no cell to be highlighted in.
  function stepMonth(delta) {
    var at = new Date(month.y, month.m + delta, 1);
    month = { y: at.getFullYear(), m: at.getMonth() };
    loadDays(begin()).catch(report);
  }

  function changeRecorder() {
    recorder = $('recordingsRecorder').value;

    // A page that has no day yet has to find one, and the day it finds belongs to
    // the recorder that was just chosen: that is step 1, so the whole load runs.
    if (selectedDay === null) { loadRecordings(); return; }

    var token = begin();
    loadDays(token)
      .then(function () { return current(token) ? loadDay(token) : undefined; })
      .catch(report);
  }

  // The event says only that something changed; what it changed to is read from the
  // API like everything else.
  function onRecordingChanged() {
    if (refreshTimer !== null) { clearTimeout(refreshTimer); }
    refreshTimer = setTimeout(function () {
      refreshTimer = null;
      if ($('pageRecordings').hidden) { dirty = true; return; }
      loadRecordings();
    }, REFRESH_DEBOUNCE_MS);
  }

  // app-core.js owns the router and is loaded first, so the page's `hidden` is
  // already up to date by the time this runs.
  window.addEventListener('hashchange', function () {
    if ($('pageRecordings').hidden) { return; }
    syncRecorderOptions();
    // **A page that never found its day is reloaded whether or not anything changed.**
    // There is nothing on it to keep, and the reason it has no day may be that the
    // first listing was answered before the server's index was built -- no
    // `recording` event follows that, so opening the page is the only occasion left.
    if (!dirty && selectedDay !== null) { return; }
    dirty = false;
    loadRecordings();
  });

  $('calendarPrev').addEventListener('click', function () { stepMonth(-1); });
  $('calendarNext').addEventListener('click', function () { stepMonth(1); });
  $('recordingsRecorder').addEventListener('change', changeRecorder);

  PRA.recordings = {
    encodePath: encodePath,
    loadRecordings: loadRecordings,
    onRecordingChanged: onRecordingChanged,
    // Called from app.js whenever the recorder snapshot is redrawn: the names in the
    // filter come from `state.recorderNames`, which only that path updates.
    syncRecorderOptions: syncRecorderOptions
  };
})();
