// ProcessRecorderApp remote control UI -- the list of recordings.
//
// The table and the two ways a row can be played: a plain `<video src>` for a file
// whose `moov` is finished, and the follow-along MSE path (app-player.js) for a
// fragmented one. Which of the two a row gets is decided here; how each one plays
// is not this file's business.
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

  // Each segment is escaped on its own: the separators are part of the route and
  // must survive, everything else in a filename may not be URL-safe.
  function encodePath(path) {
    return path.split('/').map(encodeURIComponent).join('/');
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
        play.className = 'btn-primary';
        play.textContent = 'Play';
        // A fragmented recording can be played while it is still being written:
        // its header is complete from the first byte. Anything else has no finished
        // moov box until the recording stops, so only the bytes are offered.
        play.disabled = file.inProgress && !file.fragmented;
        play.addEventListener('click', function () {
          if (file.fragmented) { player.startFollow(url, file.path); return; }
          player.stopFollow('');
          var element = $('player');
          element.src = url;
          element.play().catch(function () { /* the browser decides; the controls remain */ });
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

  PRA.recordings = {
    encodePath: encodePath,
    loadRecordings: loadRecordings
  };
})();
