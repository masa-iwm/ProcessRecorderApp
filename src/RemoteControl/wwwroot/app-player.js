// ProcessRecorderApp remote control UI -- playback. No framework, no third-party script.
//
// Two players live here and they never overlap: the live preview of a recorder
// (`#previewPlayer`, either the chunked `preview.mp4` or the DASH presentation) and
// the follow-along playback of a recording (`#player`, fragmented MP4 over MSE).
// Both are driven entirely by this file, including the listeners app.js attaches to
// the two <video> elements -- the state those listeners read is this file's.
'use strict';

(function () {
  var PRA = window.PRA;
  var core = PRA.core;
  var $ = core.$;
  var status = core.status;
  var describe = core.describe;
  var formatSize = core.formatSize;
  var showLogin = core.showLogin;
  var getJson = core.getJson;
  var send = core.send;
  var allows = core.allows;

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

  // How many rounds a full SourceBuffer may free nothing before the reason is put
  // on screen. The retry itself is unbounded on purpose -- a position that starts
  // moving again resolves it by itself -- but a position that does not move never
  // will: paused playback, and autoplay the browser refused, both leave
  // `currentTime` where it is, and without this the player would stay blank and
  // silent for as long as the tab is open.
  var FOLLOW_STALLED_TRIM_LIMIT = 10;

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

  // ---- the fragment index ----
  //
  // The file has no table that maps a moment to a byte: `moov` is never rewritten, so
  // its duration stays 0, and no `sidx` is written either. The server reads the `moof`
  // boxes and answers with one, and that answer is the whole of what arbitrary seeking
  // stands on. **It is optional.** Without it everything behaves as it did before:
  // following the live edge works and a skip is clamped into what has been buffered.

  // How often the index is asked for what has been written since its last answer. The
  // same beat as the byte polling -- one fragment a second is what arrives.
  var FOLLOW_INDEX_POLL_MS = 1000;

  // How many leading bytes are held while the size of the init segment is still
  // unknown. `ftyp` + `moov` measures a few hundred bytes; past this the index is given
  // up on rather than the buffer grown.
  var FOLLOW_INIT_CAPTURE_MAX = 1024 * 1024;

  // `{timescale, initSize, fragments, nextOffset, totalDuration, inProgress}`, or null
  // while there is none. `fragments` grows as the recording does.
  var followIndex = null;

  // The init segment, cut out of the leading bytes that were read anyway. A seek has to
  // append it again: `remove(0, Infinity)` frees the parsed one along with the media.
  var followInit = null;

  var followIndexUrl = null;
  var followIndexTimer = null;

  // Set by `followFile`, because the state a seek has to move is that function's.
  var followSeekTo = null;
  var followIndexChanged = null;

  // Set once, when `#player`'s shell is built: how the bar is told that the seek
  // control may be operated, and how far it reaches.
  var followSetSeekable = null;

  // `seekTo` assigns `currentTime`, which raises the very event that calls it.
  var followReseeking = false;

  // Whether the file being followed is still being written. Only the player shell
  // reads it: a recording that has stopped growing has no live edge, so the LIVE
  // badge and the "go live" button come off the bar for it.
  var followInProgress = false;

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

  // The index and everything cut out of it. The bar is told at the same moment: a seek
  // control left operable over a player with no index is one that does nothing.
  function releaseFollowIndex() {
    if (followIndexTimer !== null) {
      clearTimeout(followIndexTimer);
      followIndexTimer = null;
    }
    followIndex = null;
    followInit = null;
    followIndexUrl = null;
    followSeekTo = null;
    followIndexChanged = null;
    if (followSetSeekable !== null) { followSetSeekable(null); }
  }

  // Everything the <video> element holds on to: the element itself keeps decoding
  // and the object URL keeps the MediaSource alive until both are let go.
  function releaseFollowPlayer() {
    followOnFailure = null;
    followInProgress = false;
    releaseFollowIndex();
    var video = $('player');
    video.pause();
    video.removeAttribute('src');
    video.load();
    releaseFollowUrl();
  }

  function stopFollow(message) {
    // A transcode is another supply side for this same element, and it is not held by
    // any of the state below: whatever stops the follow stops it too, or a tab that
    // moved on would go on holding an auxiliary encoder slot.
    stopTranscode();

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

        // The index is asked for now, alongside the first bytes: the init segment is
        // cut out of those bytes, so both halves are wanted from the same moment.
        followIndexUrl = indexUrlFor(url);
        loadIndex(generation, 0);

        var codecs = response.headers.get('X-Codecs') || FOLLOW_CODECS_FALLBACK;
        var video = $('player');
        var source = new MediaSource();

        releaseFollowUrl();
        followUrl = URL.createObjectURL(source);
        followLive = true;
        followSeekTarget = null;
        video.src = followUrl;

        // **Once, and only once.** `sourceopen` is not a one-shot event: a MediaSource
        // that reached `ended` (a finished recording, after `endOfStream()`) goes back
        // to `open` the moment `remove()` is called on one of its buffers, and that is
        // exactly what a seek does. A listener left attached would then add a second
        // SourceBuffer and start a second `followFile` over a response body that has
        // already been read to its end -- taking the state a seek moves with it.
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
        }, { once: true });
      })
      .catch(function (error) {
        if (generation !== followGeneration || error.name === 'AbortError') { return; }
        status($('playerStatus'), error.message, true);
      });
  }

  // The index lives beside the bytes on a route of its own. `{*path}` catches
  // everything to the end of the URL, so the kind of answer cannot be a suffix -- every
  // derived API takes the `/api/recording-<kind>/` shape for that reason.
  var FOLLOW_MEDIA_PREFIX = '/api/recordings/';
  var FOLLOW_INDEX_PREFIX = '/api/recording-fragments/';

  function indexUrlFor(url) {
    return url.indexOf(FOLLOW_MEDIA_PREFIX) === 0
      ? FOLLOW_INDEX_PREFIX + url.substring(FOLLOW_MEDIA_PREFIX.length)
      : null;
  }

  // **Nothing here reports a failure onto the page.** A file with no index (an older
  // one, a request that did not arrive) plays exactly as it did before arbitrary
  // seeking existed, and saying so would be noise over a player that works.
  function loadIndex(generation, from) {
    if (followIndexUrl === null) { return; }

    fetch(followIndexUrl + '?from=' + from, { credentials: 'same-origin', cache: 'no-store' })
      .then(function (response) {
        if (generation !== followGeneration) { return undefined; }
        if (!response.ok) { throw new Error('HTTP ' + response.status); }
        return response.json();
      })
      .then(function (body) {
        if (body === undefined || generation !== followGeneration) { return; }
        applyIndex(body, from);
      })
      .catch(function () {
        // No index: the player keeps the behaviour it had without one. But **a round
        // that failed must not end the polling** -- a recording is still growing after
        // one request went wrong, and dropping the timer here freezes the length the
        // bar reaches for the rest of the playback. Only a file that already has an
        // index is retried; the first request failing is the "there is none" case.
        if (generation !== followGeneration || followIndex === null || !followIndex.inProgress) { return; }
        scheduleIndexPoll(generation);
      });
  }

  // The next round of the index polling. One timer, whichever side asks for it.
  function scheduleIndexPoll(generation) {
    if (followIndexTimer !== null) { return; }

    followIndexTimer = setTimeout(function () {
      followIndexTimer = null;
      if (generation !== followGeneration || followIndex === null) { return; }
      loadIndex(generation, followIndex.nextOffset);
    }, FOLLOW_INDEX_POLL_MS);
  }

  // How far the seek bar reaches. **Told only once a seek could actually be carried
  // out**: `seekTo` needs the init segment as much as it needs the index, and a control
  // that answers a drag by doing nothing reads as broken rather than as unavailable.
  // Either half can be the later one, so both call this.
  function refreshSeekable() {
    if (followSetSeekable === null || followIndex === null || followInit === null) { return; }
    // A file whose fragments are all still to come has nothing to seek within yet.
    if (followIndex.fragments.length === 0) { return; }

    followSetSeekable(followIndex.totalDuration / followIndex.timescale);
  }

  // The server always counts from the beginning, so `initSize`, `nextOffset` and
  // `totalDuration` describe the whole file however small `from` made the answer;
  // only `fragments` is the part that has to be appended rather than replaced.
  function applyIndex(body, from) {
    if (followIndex === null || from === 0) {
      followIndex = {
        timescale: body.timescale,
        initSize: body.initSize,
        fragments: body.fragments,
        nextOffset: body.nextOffset,
        totalDuration: body.totalDuration,
        inProgress: body.inProgress
      };
    } else {
      for (var i = 0; i < body.fragments.length; i++) {
        followIndex.fragments.push(body.fragments[i]);
      }
      followIndex.nextOffset = body.nextOffset;
      followIndex.totalDuration = body.totalDuration;
      followIndex.inProgress = body.inProgress;
    }

    if (followIndexChanged !== null) { followIndexChanged(); }
    refreshSeekable();

    // A file that is no longer being written has an index that is final.
    if (!followIndex.inProgress) { return; }
    scheduleIndexPoll(followGeneration);
  }

  // Whether a moment is somewhere the element could play right now.
  function insideBuffered(video, seconds) {
    var ranges = video.buffered;
    for (var i = 0; i < ranges.length; i++) {
      if (ranges.start(i) <= seconds && seconds <= ranges.end(i)) { return true; }
    }
    return false;
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
    // Consecutive rounds in which a full SourceBuffer asked for a trim that freed
    // nothing. Reset by anything that makes progress (a trim that cut, an append
    // that took).
    var stalledTrims = 0;
    // Whether the one unconditional jump to the live edge has been made.
    var joined = false;

    // The leading bytes, held until the index says how many of them are the init
    // segment. They are the arrays that go into the SourceBuffer too -- `appendBuffer`
    // copies, so keeping them costs the init segment's size once the cut is made.
    var head = [];
    var headBytes = 0;

    // Which half of a seek is waiting for `updateend`: 'removing' while the old media
    // is being freed, 'appending' while the init segment goes back in, null otherwise.
    var seekPhase = null;
    var seekSeconds = 0;

    // Bumped by every seek. A chunk that was already on its way when the seek happened
    // belongs to the byte range that was abandoned: counting it into `next` would leave
    // a hole in the stream, and a byte stream with a hole never recovers.
    var epoch = 0;

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
            if (trimFollow(buffer, true)) {
              stalledTrims = 0;
              return;
            }

            stalledTrims++;
            if (stalledTrims === FOLLOW_STALLED_TRIM_LIMIT) {
              status($('playerStatus'),
                'the buffer is full and nothing can be freed while the position stands still', true);
            }
            // With an index there is a way out that does not need the position to
            // move: free everything and fetch the fragment the position stands in
            // again. Without one the only thing left is to wait for it to advance.
            if (stalledTrims < FOLLOW_STALLED_TRIM_LIMIT && seekTo(video.currentTime, true)) { return; }
            scheduleFlush();
            return;
          }
          fail('append failed: ' + error.message);
          return;
        }
        queue.shift();
        trimmed = false;
        stalledTrims = 0;
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
      var mine = epoch;
      reading = true;
      receivedBytes = false;
      var reader = response.body.getReader();

      function step() {
        return reader.read().then(function (chunk) {
          if (generation !== followGeneration || mine !== epoch) { return undefined; }
          if (chunk.done) {
            reading = false;
            flush();
            return undefined;
          }

          captureHead(chunk.value);
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
      var mine = epoch;
      followAbort = controller;

      fetch(url, {
        signal: controller.signal,
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { 'Range': 'bytes=' + next + '-' }
      }).then(function (response) {
        if (generation !== followGeneration || mine !== epoch) { return undefined; }
        if (response.status === 401) { showLogin('Sign in to continue.'); return undefined; }

        inProgress = response.headers.get('X-In-Progress') !== 'false';
        followInProgress = inProgress;

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
      // A seek owns the SourceBuffer until both of its operations are done; nothing
      // else may append into the middle of them.
      if (seekPhase !== null) { continueSeek(); return; }
      applyIndexDuration();
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

    // ---- the index, and the arbitrary seeking it makes possible ----

    function captureHead(chunk) {
      if (followInit !== null || FOLLOW_INIT_CAPTURE_MAX < headBytes) { return; }
      head.push(chunk);
      headBytes += chunk.byteLength;
      cutInit();
    }

    // The init segment is the file's first `initSize` bytes, and those have been read
    // already: cutting them out of the stream costs nothing and needs no second request.
    function cutInit() {
      if (followInit !== null || followIndex === null) { return; }

      var size = followIndex.initSize;
      if (size <= 0 || headBytes < size) { return; }

      var leading = new Uint8Array(headBytes);
      var at = 0;
      for (var i = 0; i < head.length; i++) {
        leading.set(head[i], at);
        at += head[i].byteLength;
      }
      followInit = leading.subarray(0, size);
      head = [];
      // The other half of what seeking stands on has just arrived; the bar may open now.
      refreshSeekable();
    }

    // **Without a duration the element refuses to seek at all.** `moov` says 0 and MSE
    // starts at NaN, so the length the index counted is the only one there is.
    function applyIndexDuration() {
      if (followIndex === null || buffer.updating || source.readyState !== 'open') { return; }

      var length = followIndex.totalDuration / followIndex.timescale;
      if (!isFinite(length) || length <= 0) { return; }

      try {
        if (followIndex.inProgress) {
          // A file still being written has no end to declare; the seekable range is
          // what says how much of it exists so far.
          if (source.duration !== Infinity) { source.duration = Infinity; }
          source.setLiveSeekableRange(0, length);
          return;
        }
        // Never below what is already buffered: MSE frees the media past a shortened
        // duration, which would cut the end off a finished recording.
        if (bufferedEndOf(video) <= length) { source.duration = length; }
      } catch (error) {
        /* the next updateend tries again */
      }
    }

    // Which fragment carries a moment **and may be started from**: only one whose first
    // sample is a sync sample can begin a decode. Fragments are a second and the key
    // frame interval is two, so about every other one cannot.
    function fragmentFor(seconds) {
      var fragments = followIndex.fragments;
      if (fragments.length === 0) { return null; }

      var time = seconds * followIndex.timescale;
      var chosen = null;
      for (var i = 0; i < fragments.length; i++) {
        if (time < fragments[i].time) { break; }
        if (fragments[i].sync) { chosen = fragments[i]; }
      }
      return chosen === null ? fragments[0] : chosen;
    }

    // Landing within catch-up distance of the live edge is joining it, not leaving it:
    // the operator dragged the bar to "now". A file that is no longer written has no
    // edge to join.
    function restoreFollowLive(seconds) {
      followLive = followIndex !== null && followIndex.inProgress
        && followIndex.totalDuration / followIndex.timescale - FOLLOW_CATCHUP_LAG_SECONDS <= seconds;
    }

    // **The only way to a position that is not buffered.** The reading loop only ever
    // goes forward from where it is, so the supply is torn down and started again at
    // the fragment the index points at.
    //
    // `restart` skips the shortcut below: the caller (a SourceBuffer that is full and
    // cannot be trimmed) needs the media freed even though the position is inside it.
    function seekTo(seconds, restart) {
      if (followIndex === null || followInit === null) { return false; }

      var target = Math.max(0, seconds);

      // **A seek asked for while the previous one is still freeing the media only has
      // to change its mind.** Nothing has been fetched for it yet -- the old fetch is
      // already abandoned and the queue already empty -- so moving where the resumed
      // loop starts and where the position lands is the whole of it. Neither branch
      // below applies: `abort()` throws while a range removal is running (the value
      // would be dropped and the drag would look ignored), and what is still in
      // `buffered` is on its way out, so the shortcut would land the position in media
      // that is about to be freed. This is the path a dragged seek bar takes -- `input`
      // fires many times before one removal ends.
      if (seekPhase === 'removing') {
        var pending = fragmentFor(target);
        if (pending === null) { return false; }

        next = pending.offset;
        seekSeconds = target;
        return true;
      }

      if (!restart && insideBuffered(video, target)) {
        followSeekTarget = target;
        video.currentTime = target;
        restoreFollowLive(target);
        return true;
      }

      var fragment = fragmentFor(target);
      // `remove` needs a duration to measure against, and MSE starts at NaN.
      if (fragment === null || isNaN(video.duration)) { return false; }

      // **`abort()` is what resets the parser, and `remove()` is not.** Appends land on
      // arbitrary byte boundaries, so the SourceBuffer is usually holding half a media
      // segment; putting an init segment after that half is a parse error.
      try {
        if (source.readyState === 'open') { buffer.abort(); }
        buffer.remove(0, Infinity);
      } catch (error) {
        return false;   // nothing has been torn down yet
      }

      epoch++;
      if (followAbort !== null) { followAbort.abort(); followAbort = null; }
      if (followTimer !== null) { clearTimeout(followTimer); followTimer = null; }

      reading = false;
      receivedBytes = false;
      queue = [];
      next = fragment.offset;
      total = null;
      trimmed = false;
      stalledTrims = 0;
      // The one unconditional jump to the live edge belongs to the start of playback,
      // not to a position that was asked for.
      joined = true;
      followLive = false;
      seekSeconds = target;
      seekPhase = 'removing';
      return true;
    }

    // A SourceBuffer takes one operation at a time: the removal has to finish before
    // the init segment can go in, and that append before the position may be asked for.
    function continueSeek() {
      // `abort()` raises an `updateend` of its own when it cancelled an append. That
      // one arrives while the removal is still running, and the next operation cannot
      // be started on a SourceBuffer that is busy.
      if (buffer.updating) { return; }

      if (seekPhase === 'removing') {
        seekPhase = 'appending';
        try {
          buffer.appendBuffer(followInit);
        } catch (error) {
          seekPhase = null;
          fail('seek failed: ' + error.message);
        }
        return;
      }

      seekPhase = null;
      // Recorded before the assignment: `seeking` arrives as a task, and the listener
      // must not read this as the operator taking over from the following.
      followSeekTarget = seekSeconds;
      video.currentTime = seekSeconds;
      restoreFollowLive(seekSeconds);
      applyIndexDuration();
      request();
    }

    followSeekTo = seekTo;
    followIndexChanged = function () {
      cutInit();
      applyIndexDuration();
    };

    inProgress = first.headers.get('X-In-Progress') !== 'false';
    followInProgress = inProgress;
    total = totalOf(first, 0);
    // The first body is read outside `request()`, so it needs the same catch:
    // a connection lost while it is being read otherwise rejects into nothing and
    // the player stops without a word.
    read(first).catch(function (error) {
      if (generation !== followGeneration || error.name === 'AbortError') { return; }
      fail(error.message);
    });
  }

  // Stay a whole safety margin behind where playback is. **`remove(a, b)` does not
  // stop at `b`:** it frees up to the first random access point at or after `b`, so
  // asking to cut up to the playback position frees the GOP that is playing. What
  // that looks like depends on the MediaSource: while it is still open the element
  // re-buffers at the next range, and once it is `ended` there is nothing to wait
  // for, so the element ends playback and lands on `duration` (measured with the
  // margin removed, on a finished 90 second file: `buffered.start` 18.000 and the
  // position pinned at 90; with the margin, 12.000 and a position that keeps going).
  // The margin is larger than two key frame intervals, which is as far as a
  // random access point can be. L1 (`WebAssetManifestTests`) reads this
  // declaration and holds it above `2 * EncoderCatalog.TargetKeyframeIntervalSeconds`,
  // so lowering it here alone fails.
  //
  // `force` still keeps the margin -- the caller has been told the SourceBuffer is
  // full, but freeing the media that is being decoded does not help it play.
  var FOLLOW_TRIM_SAFETY_SECONDS = 5;

  function trimFollow(buffer, force) {
    var video = $('player');
    var ranges = video.buffered;
    if (ranges.length === 0) { return false; }

    var start = ranges.start(0);
    var end = ranges.end(ranges.length - 1);

    // Compare the span, not the end: `end` passes 70 once and stays past it.
    if (!force && end - start <= FOLLOW_TRIM_TRIGGER_SECONDS) { return false; }

    var safe = video.currentTime - FOLLOW_TRIM_SAFETY_SECONDS;
    var cut = force ? safe : Math.min(end - FOLLOW_WINDOW_SECONDS, safe);

    // Nothing can be freed yet. A file opened from its beginning is here (the
    // position is a fraction of a second, so `cut` is negative) and so is a
    // QuotaExceededError raised that early -- the caller waits and tries again
    // once the position has advanced, which leaves the buffer shallow rather
    // than stopping playback.
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

  // The way back to the live edge after a seek took the following down. The jump is
  // the same one `followEdge` makes (`FOLLOW_CATCHUP_LAG_SECONDS` behind the buffered
  // end, never backwards), and the position asked for is recorded the same way, so
  // the `seeking` listener does not read this as another seek by the user.
  function resumeFollow() {
    followLive = true;

    var video = $('player');
    var ranges = video.buffered;
    if (ranges.length === 0) { return; }

    var target = ranges.end(ranges.length - 1) - FOLLOW_CATCHUP_LAG_SECONDS;
    if (target <= video.currentTime) { return; }
    followSeekTarget = target;
    video.currentTime = target;
  }

  // ---- transcoded playback of a finished recording ----
  //
  // The same file, re-encoded to a smaller frame while it is read. The server owns one
  // pipeline per session and answers with a fragmented MP4 that starts at the position
  // that was asked for, so this side does what the live preview does with its chunked
  // body -- plus three things the preview has no need of:
  //
  // 1. `mode = 'sequence'` with `timestampOffset = start`. The response always begins at
  //    presentation time zero (it is a fresh pipeline, not a cut out of the file), so the
  //    offset is what puts the picture back onto the recording's own timeline and keeps
  //    the clock and the seek bar honest.
  // 2. **The reading is held back.** The server converts as fast as the machine can
  //    (`sync=false`), and reading to the end would pull a whole recording through an
  //    encoder for someone watching its first minute. While more than
  //    TRANSCODE_READ_AHEAD_SECONDS is buffered ahead of the position, `read()` is not
  //    called at all: the response stays open and the server blocks on its own appsink.
  //    **Closing it instead would give the auxiliary encoder slot away**, and getting one
  //    back is not something this page can promise.
  // 3. A seek is a new request on the **same session id**, which is what lets the server
  //    hand the slot from the old pipeline to the new one. A new id would ask for a
  //    second slot while still holding the first.
  // 4. **One request at a time, and one target waiting for it.** A dragged seek bar fires
  //    `input` many times, and each of them reaches this side. `transcodePhase` says why
  //    the buffer cannot answer right now -- 'removing' while a range removal runs (a
  //    SourceBuffer takes one operation at a time and `abort()` throws while it does),
  //    'requesting' from the moment a request goes out until the SourceBuffer it will be
  //    appended to exists. In either state a seek only writes `transcodeSeekAt` and
  //    returns; whoever ends the phase (the removal's `updateend`, or the answer
  //    arriving) takes that target. So a whole drag costs one request in flight plus at
  //    most one behind it, rather than one pipeline per `input`.
  //
  // Which quality is playing is this page's business alone -- the server keeps no
  // selection -- so a reload comes back on the recording's own stream.

  var TRANSCODE_PREFIX = '/api/recording-transcode/';

  // The server's word for "every auxiliary encoder is in use". It arrives as the `error`
  // of a 409, from this route and from the DASH preview's three, and it is the one
  // refusal that repairs itself: the SSE `state` says when a slot came back.
  var TRANSCODE_BUSY = 'auxiliary encoder busy';

  // How far ahead of the position the response is allowed to run before reading stops,
  // and how long to wait before looking again.
  var TRANSCODE_READ_AHEAD_SECONDS = 60;
  var TRANSCODE_RESUME_MS = 500;

  // How far the target a seek left behind has to be from the position the answer on its
  // way was asked for before that answer is dropped and the request made again. Below it
  // the difference is not worth a pipeline: the media that is arriving already covers it.
  var TRANSCODE_RETARGET_SECONDS = 0.25;

  // The presets the server offers, largest first. **Their heights are the whole of what
  // is mirrored here**: which of them apply to a recording is `preset.height <= its
  // height`, the same rule the server uses for the live preview, and the frame each one
  // resolves to is never computed on this side (only the supply side sees the caps).
  var TRANSCODE_PRESETS = [
    { id: '1080p', label: '1080p', height: 1080 },
    { id: '720p', label: '720p', height: 720 },
    { id: '480p', label: '480p', height: 480 },
    { id: '360p', label: '360p', height: 360 }
  ];

  // One id for the lifetime of the page. Every request this tab makes carries it, so the
  // server sees one session however often the quality or the position changes.
  var transcodeSession = null;

  // What the recordings page last handed over: the row `#player` is showing. Both the
  // original and every transcode of it are opened from this one record.
  var currentRecording = null;

  var transcodeActive = false;
  var transcodeQuality = null;
  var transcodeStart = 0;

  // Bumped by every attempt. **An answer that is no longer the newest attempt installs
  // nothing** -- a dragged seek bar can put several requests in flight, and the one that
  // arrives last is not necessarily the one that was asked for last.
  var transcodeRequests = 0;

  // Bumped by every install and every stop: a pending read, an `updateend` or a timer
  // belonging to an older stream returns without touching anything.
  var transcodeGeneration = 0;

  var transcodeAbort = null;
  var transcodeTimer = null;
  var transcodeReadTimer = null;
  var transcodeSource = null;
  var transcodeBuffer = null;

  // The running stream's `flush`, so that the one `updateend` listener on the SourceBuffer
  // can reach whichever stream owns it now.
  var transcodeFlush = null;

  // Why the SourceBuffer cannot answer a seek right now (point 4 above): 'removing' while
  // a seek is freeing the media it is about to replace -- nothing may be appended in
  // between -- 'requesting' from the moment a request goes out until the buffer it will
  // be appended to exists, null when the buffer is the playback's own to seek within.
  var transcodePhase = null;

  // Where the seek that is waiting on that phase wants to land, and whether one is
  // waiting at all. **At most one is kept**: a drag that has moved on has said what it
  // wants, and the positions it passed over on the way are not wanted at all.
  var transcodeSeekAt = 0;
  var transcodeLatched = false;

  // Whether the playback that is being rebuilt was running. Read when the rebuild starts,
  // because by the time the first chunk of the answer lands the element has been emptied
  // and is paused whatever the user asked for.
  var transcodeResume = true;

  // The position this file asked the element to move to, recognised the same way the
  // follow's corrections are: `seeking` is delivered as a task and the element has
  // usually moved a little past it by then.
  var transcodeSeekTarget = null;

  // Set by a 409 and cleared by the first `state` that reports a free slot. The menu reads
  // it so that a refusal is visible on the entries that were refused, not only in the
  // status line.
  var transcodeBusy = false;

  // What the last SSE `state` said. null until one arrives -- unknown is not "none free",
  // and greying the menu out before the first event would make the feature look absent.
  var auxiliaryEncodersFree = null;

  function transcodeSessionId() {
    if (transcodeSession === null) {
      // The server accepts [A-Za-z0-9_-]{1,64}, which a UUID satisfies as it is written.
      transcodeSession = window.crypto && window.crypto.randomUUID
        ? window.crypto.randomUUID()
        : 'tc-' + Date.now().toString(36) + '-' + Math.random().toString(36).substring(2);
    }
    return transcodeSession;
  }

  // Beside the bytes, on a route of its own -- the same shape as the fragment index, and
  // for the same reason: `{*path}` reaches the end of the URL, so the kind of answer
  // cannot be a suffix.
  function transcodeUrlFor(url) {
    return url.indexOf(FOLLOW_MEDIA_PREFIX) === 0
      ? TRANSCODE_PREFIX + url.substring(FOLLOW_MEDIA_PREFIX.length)
      : null;
  }

  // Everything a transcode holds, without touching the element: `stopFollow` owns the
  // teardown of `#player` and calls this on its way through, so releasing the element
  // here as well would tear it down twice.
  function stopTranscode() {
    transcodeRequests++;
    transcodeGeneration++;
    if (transcodeAbort !== null) {
      transcodeAbort.abort();
      transcodeAbort = null;
    }
    if (transcodeTimer !== null) {
      clearTimeout(transcodeTimer);
      transcodeTimer = null;
    }
    if (transcodeReadTimer !== null) {
      clearTimeout(transcodeReadTimer);
      transcodeReadTimer = null;
    }
    transcodeActive = false;
    transcodeQuality = null;
    transcodeSource = null;
    transcodeBuffer = null;
    transcodeFlush = null;
    transcodePhase = null;
    transcodeLatched = false;
    transcodeResume = true;
    transcodeSeekTarget = null;
  }

  // The end of a request, whatever it answered. The phase it set is dropped, and a seek
  // that arrived while it was on the wire is taken now -- there is nothing left to wait
  // for. Only the newest attempt may do this; an older one that finally comes back would
  // otherwise clear the phase a newer request is holding.
  function endTranscodeRequest() {
    transcodePhase = null;
    if (!transcodeLatched) { return; }

    transcodeLatched = false;
    if (transcodeActive) { restartTranscodeAt(transcodeSeekAt); }
  }

  // Where a seek that arrived while a request was on the wire wants to land, when that is
  // far enough from what the request asked for to be worth building the pipeline again.
  // Null means the answer in hand already covers it, drag or no drag.
  function transcodeRetargetFor(start) {
    if (!transcodeLatched) { return null; }
    return TRANSCODE_RETARGET_SECONDS < Math.abs(transcodeSeekAt - start) ? transcodeSeekAt : null;
  }

  // **The request comes first and the player is rebuilt from its answer.** The refusals
  // this route has are ordinary answers (no slot, this machine cannot transcode, the file
  // is still being written), and a switch that tore the playback down before asking would
  // turn every one of them into a black rectangle.
  function requestTranscode(id, start, reuse) {
    var file = currentRecording;
    if (file === null || typeof MediaSource === 'undefined') { return; }

    var base = transcodeUrlFor(file.url);
    if (base === null) { return; }

    var attempt = ++transcodeRequests;
    var controller = new AbortController();
    var url = base
      + '?start=' + encodeURIComponent(Math.max(0, start).toFixed(3))
      + '&q=' + encodeURIComponent(id)
      + '&session=' + encodeURIComponent(transcodeSessionId());

    // Held from here until the answer has a SourceBuffer behind it: while it is set, a
    // seek only moves the target it will land on, so a dragged bar cannot put a second
    // request on the wire for a position it has already left.
    transcodePhase = 'requesting';

    fetch(url, { signal: controller.signal, credentials: 'same-origin', cache: 'no-store' })
      .then(function (response) {
        // Superseded while this was in flight: the body is not read and the connection is
        // dropped, which is what tells the server to let the pipeline behind it go. The
        // phase belongs to the newer request now and is left where it is.
        if (attempt !== transcodeRequests) { controller.abort(); return undefined; }
        if (response.status === 401) {
          endTranscodeRequest();
          showLogin('Sign in to continue.');
          return undefined;
        }
        if (!response.ok) {
          endTranscodeRequest();
          return refuseTranscode(response);
        }

        installTranscode(response, controller, id, Math.max(0, start), reuse === true);
        return undefined;
      })
      .catch(function (error) {
        if (attempt !== transcodeRequests || error.name === 'AbortError') { return; }
        endTranscodeRequest();
        status($('playerStatus'), error.message, true);
      });
  }

  // A refusal changes nothing that is playing. The 409 is remembered as well as shown: the
  // quality menu greys its presets out until a slot is reported free again.
  function refuseTranscode(response) {
    return response.json().catch(function () { return {}; }).then(function (body) {
      if (response.status === 409 && body.error === TRANSCODE_BUSY) { transcodeBusy = true; }
      status($('playerStatus'), describe(body, response.status), true);
    });
  }

  function installTranscode(response, controller, id, start, reuse) {
    var codecs = response.headers.get('X-Codecs') || FOLLOW_CODECS_FALLBACK;
    var served = response.headers.get('X-Transcode-Quality') || id;
    var mime = 'video/mp4; codecs="' + codecs + '"';

    // **Read before anything is torn down.** Where the seek bar went while this answer was
    // on its way is what the playback has to end up at.
    var retarget = transcodeRetargetFor(start);
    transcodeLatched = false;
    transcodePhase = null;

    // **A refusal, not a failure.** Nothing this page can do makes a codec the browser
    // will not decode playable, so the body is dropped unread -- which is what lets the
    // server fold the pipeline -- and whatever is playing is left exactly as it is. The
    // same reading the DASH preview does of a representation it cannot take.
    if (typeof MediaSource === 'undefined' || !MediaSource.isTypeSupported(mime)) {
      controller.abort();
      status($('playerStatus'), 'this browser cannot play ' + mime, true);
      return;
    }

    // A seek keeps the presentation it already has: the element's position is where the
    // user put it, and building a new MediaSource would send it back to zero and lose it.
    if (reuse && transcodeBuffer !== null && transcodeSource !== null
        && transcodeSource.readyState === 'open') {
      transcodeGeneration++;
      transcodeAbort = controller;
      transcodeActive = true;
      transcodeQuality = served;
      transcodeStart = start;

      // The bar moved on while this was in flight. The buffer is emptied again for where
      // it went -- one more request behind the one that is being dropped, never one per
      // `input`, because the phase that starts here latches everything that follows.
      if (retarget !== null) {
        restartTranscodeAt(retarget);
        return;
      }

      // `place()` is asked for even here: the removal that preceded this request emptied
      // the buffer, so the position has nothing under it until the first chunk lands, and
      // the call only ever moves forward to the first sample that arrives.
      pumpTranscode(response, transcodeGeneration, transcodeBuffer, transcodeSource, 0 < start);
      return;
    }

    // Whatever owns the element now is let go first -- including a transcode, which
    // `stopFollow` stops on its way through. **It resets the resume intent as well**, and
    // this rebuild is not what decided it: a seek taken while the user has the playback
    // paused reaches here too, whenever the length is unknown and nothing can be removed.
    var resume = transcodeResume;
    stopFollow('');
    transcodeResume = resume;

    var generation = ++transcodeGeneration;
    transcodeAbort = controller;
    transcodeActive = true;
    transcodeQuality = served;
    transcodeStart = start;

    // `stopFollow` cleared the phase on its way through `stopTranscode`, and it goes back:
    // **the SourceBuffer does not exist until `sourceopen`**, and a seek arriving in that
    // window has nothing to remove from -- it would build a second MediaSource, and the
    // position would be thrown back to zero for each one.
    transcodePhase = 'requesting';

    var video = $('player');
    var source = new MediaSource();
    releaseFollowUrl();
    followUrl = URL.createObjectURL(source);
    video.src = followUrl;
    status($('playerStatus'), 'transcoding ' + served + '...', false);

    // **Once, and only once**, for the reason the follow documents: a MediaSource that
    // reached `ended` goes back to `open` when one of its buffers is removed from, and a
    // second listener would add a second SourceBuffer over a body already read to its end.
    source.addEventListener('sourceopen', function () {
      if (generation !== transcodeGeneration) { return; }

      var buffer;
      try {
        buffer = source.addSourceBuffer(mime);
      } catch (error) {
        // The element is already holding this empty MediaSource (`stopFollow` handed it
        // over above), so letting the state go is not enough -- the element has to be
        // released with it or a black rectangle stays where the playback was.
        failTranscode(error.message);
        return;
      }

      // 'sequence', not 'segments': the pipeline starts a timeline of its own at zero
      // however far into the file it was told to begin, so the offset is what carries the
      // position. In 'segments' the media would land at the head of the recording.
      buffer.mode = 'sequence';
      try {
        buffer.timestampOffset = start;
      } catch (error) {
        /* the media lands at zero; the picture is right and only the clock is not */
      }

      transcodeSource = source;
      transcodeBuffer = buffer;
      buffer.addEventListener('updateend', onTranscodeUpdateEnd);
      buffer.addEventListener('error', function () {
        if (generation !== transcodeGeneration) { return; }
        failTranscode('the source buffer failed');
      });

      applyTranscodeDuration(source);

      // The buffer exists, so the phase is over -- and whatever the bar asked for while
      // it lasted is taken now. **Nothing has been appended yet**, so the position is
      // reached by asking again rather than by emptying anything: the offset simply moves
      // with it, and this body is dropped unread.
      transcodePhase = null;
      // **The newest wins.** A drag that came back to where this answer starts is still
      // the last thing said, so it is the one measured against it -- taking the older
      // target because the newer one is near would send the playback where the user has
      // just left.
      var desired = transcodeLatched ? transcodeSeekAt : retarget;
      transcodeLatched = false;
      var target = desired !== null && TRANSCODE_RETARGET_SECONDS < Math.abs(desired - start)
        ? desired
        : null;
      if (target !== null) {
        controller.abort();
        try {
          buffer.timestampOffset = target;
        } catch (error) {
          /* the media lands at zero; the picture is right and only the clock is not */
        }
        requestTranscode(transcodeQuality, target, true);
        return;
      }

      pumpTranscode(response, generation, buffer, source, 0 < start);
    }, { once: true });
  }

  // The length is the listing's, not the media's: the response carries only the part that
  // was asked for, so nothing in it says how long the recording is. **Without a duration
  // the element refuses to seek at all**, which is the whole of what the bar offers here.
  function applyTranscodeDuration(source) {
    var known = currentRecording !== null && 0 < currentRecording.durationMs
      ? currentRecording.durationMs / 1000
      : 0;
    if (known <= 0) { return; }

    try {
      source.duration = known;
    } catch (error) {
      /* the bar falls back to the buffered end */
    }
    if (followSetSeekable !== null) { followSetSeekable(known); }
  }

  // A SourceBuffer takes one operation at a time, so the removal a seek starts owns it
  // until it is done; only then may the offset move and the new request be made.
  function onTranscodeUpdateEnd() {
    if (!transcodeActive || transcodeBuffer === null) { return; }

    // Only a removal ends here. 'requesting' is not an operation on the buffer, and
    // reading it as one would take an ordinary append's `updateend` for the end of a seek.
    if (transcodePhase === 'removing') {
      if (transcodeBuffer.updating) { return; }
      transcodePhase = null;
      // `transcodeSeekAt` is where the drag last put it, which is the whole point of
      // latching: every `input` after the removal started wrote it, and this takes the
      // last one rather than the one the removal began for.
      transcodeLatched = false;
      try {
        transcodeBuffer.timestampOffset = transcodeSeekAt;
      } catch (error) {
        /* the append still lands; only its place on the timeline is the old one */
      }
      requestTranscode(transcodeQuality, transcodeSeekAt, true);
      return;
    }

    if (transcodeFlush !== null) { transcodeFlush(); }
  }

  function failTranscode(reason) {
    stopTranscode();
    releaseFollowPlayer();
    status($('playerStatus'), reason, true);
  }

  // Owns everything that spans one response: the chunks waiting for the SourceBuffer,
  // whether the body has been read to its end, and whether the position has been put where
  // the media starts.
  function pumpTranscode(response, generation, buffer, source, reposition) {
    var video = $('player');
    var queue = [];
    var reading = false;
    var ended = false;
    var started = false;
    var placed = !reposition;
    var trimmed = false;
    var reader = response.body.getReader();

    function alive() { return generation === transcodeGeneration; }

    // The element outlives every stream, so its `error` listener is attached once and
    // routed through this hook -- the same shape the follow and the preview use.
    followOnFailure = function (reason) {
      if (!alive()) { return; }
      failTranscode(reason);
    };

    // The first frames land at `start`, so an element sitting at zero has nothing to
    // decode. Done once, and recorded so that the `seeking` it raises is not read as the
    // user taking over.
    function place() {
      if (placed) { return; }
      var ranges = video.buffered;
      if (ranges.length === 0) { return; }

      placed = true;
      var target = ranges.start(0);
      if (video.currentTime < target) {
        transcodeSeekTarget = target;
        video.currentTime = target;
      }
    }

    function flush() {
      if (!alive() || buffer.updating || transcodePhase === 'removing') { return; }
      place();

      // One remove() between two appends, and the flag is what stops it taking every turn:
      // a cut can free nothing (remove is granular to random access points) and would
      // otherwise ask to be repeated forever.
      if (!trimmed) {
        trimmed = true;
        if (trimFollow(buffer, false)) { return; }   // its own updateend comes back here
      }

      if (0 < queue.length) {
        var chunk = queue[0];
        try {
          buffer.appendBuffer(chunk);
        } catch (error) {
          // A full SourceBuffer is not a failure: what is in it still plays and the media
          // behind the position can be freed as the position advances. The chunk stays at
          // the head of the queue -- a byte stream with a hole never recovers.
          if (error.name === 'QuotaExceededError') {
            trimmed = false;
            if (trimFollow(buffer, true)) { return; }
            scheduleTranscodeFlush(generation, flush);
            return;
          }
          failTranscode('append failed: ' + error.message);
          return;
        }
        queue.shift();
        trimmed = false;
        // Only what was running goes on running: a seek taken while the user had the
        // element paused rebuilds the supply, and starting it here would take the pause
        // away from them. `transcodeResume` was read when the rebuild began, because by
        // now the element is paused whichever way it was.
        if (!started) {
          started = true;
          if (transcodeResume) {
            video.play().catch(function () { /* the browser decides; the controls remain */ });
          }
        }
        return;
      }

      if (!reading && ended) { finish(); }
    }

    function finish() {
      try {
        if (source.readyState === 'open') { source.endOfStream(); }
      } catch (error) {
        /* the element already has everything; nothing is left to repair */
      }
      status($('playerStatus'), 'complete (transcode ' + transcodeQuality + ')', false);
    }

    // How far the media reaches beyond the position. A negative distance (a seek forward
    // into nothing) reads as "keep reading", which is what it means.
    function readAhead() {
      var ranges = video.buffered;
      if (ranges.length === 0) { return 0; }
      return ranges.end(ranges.length - 1) - video.currentTime;
    }

    function step() {
      if (!alive()) { return; }

      // **Not a smaller read, no read at all.** The response is the back pressure: while
      // this side does not read, the server's appsink fills and its pipeline waits.
      if (TRANSCODE_READ_AHEAD_SECONDS < readAhead()) {
        reading = false;
        if (transcodeReadTimer === null) {
          transcodeReadTimer = setTimeout(function () {
            transcodeReadTimer = null;
            step();
          }, TRANSCODE_RESUME_MS);
        }
        return;
      }

      reading = true;
      reader.read().then(function (chunk) {
        if (!alive()) { return; }
        if (chunk.done) {
          reading = false;
          ended = true;
          flush();
          return;
        }
        queue.push(chunk.value);
        flush();
        step();
      }).catch(function (error) {
        if (!alive() || error.name === 'AbortError') { return; }
        failTranscode(error.message);
      });
    }

    transcodeFlush = flush;
    step();
  }

  // A retry of an append the SourceBuffer refused. One timer, shared with the read
  // throttle, which is safe because the two cannot both be wanted: a refused append means
  // the queue is not empty, and the throttle only runs when it is.
  function scheduleTranscodeFlush(generation, flush) {
    if (transcodeTimer !== null) { return; }
    transcodeTimer = setTimeout(function () {
      transcodeTimer = null;
      if (generation !== transcodeGeneration) { return; }
      flush();
    }, FOLLOW_POLL_MS);
  }

  // **The only way to a position the response has not reached.** The pipeline reads
  // forward from where it was told to start, so a position outside what is buffered is a
  // new request -- on the same session, which is what hands the encoder slot over rather
  // than asking for a second one.
  function seekTranscode(seconds) {
    if (!transcodeActive) { return false; }

    var video = $('player');
    var target = Math.max(0, seconds);

    if (video.currentTime !== target) {
      transcodeSeekTarget = target;
      video.currentTime = target;
    }

    // **Before the shortcut, not after it.** What `buffered` reports while a removal runs
    // is on its way out, and while a request is out it belongs to a position that has been
    // left, so neither may be read as "already have it" -- the seek would be answered by
    // media that is about to disappear. The target is remembered and the phase's own end
    // takes it. This is the path a dragged bar takes, `input` after `input`.
    if (transcodePhase !== null) {
      transcodeSeekAt = target;
      transcodeLatched = true;
      return true;
    }

    if (insideBuffered(video, target)) { return true; }

    restartTranscodeAt(target);
    return true;
  }

  function restartTranscodeAt(seconds) {
    // **A seek asked for while the last one is still being carried out only has to change
    // its mind** -- the same shortcut `seekTo` takes for the follow, and for the same
    // reason: `abort()` throws while a range removal runs, so the value would be dropped
    // and the drag would look ignored, and a second request would have the server build a
    // pipeline for a position that has already been passed.
    if (transcodePhase !== null) {
      transcodeSeekAt = seconds;
      transcodeLatched = true;
      return;
    }

    // Read while the element still says so: everything below empties it, and a paused
    // playback would look running by the time the first chunk lands.
    transcodeResume = !$('player').paused;

    // The connection that is running belongs to the position that was abandoned. Dropping
    // it is also what tells the server it may fold that pipeline.
    if (transcodeAbort !== null) {
      transcodeAbort.abort();
      transcodeAbort = null;
    }
    if (transcodeTimer !== null) {
      clearTimeout(transcodeTimer);
      transcodeTimer = null;
    }
    if (transcodeReadTimer !== null) {
      clearTimeout(transcodeReadTimer);
      transcodeReadTimer = null;
    }
    transcodeGeneration++;
    transcodeFlush = null;
    transcodeSeekAt = seconds;
    transcodeLatched = false;

    if (transcodeBuffer === null || transcodeSource === null
        || transcodeSource.readyState !== 'open') {
      requestTranscode(transcodeQuality, seconds, false);
      return;
    }

    // **`abort()` is what resets the parser, and `remove()` is not.** Appends land on
    // arbitrary byte boundaries, so the buffer is usually holding half a media segment,
    // and the next response starts with an init segment of its own.
    try {
      transcodeBuffer.abort();
      transcodeBuffer.remove(0, Infinity);
      transcodePhase = 'removing';
    } catch (error) {
      // `remove()` refuses while the MediaSource has no duration, which is the case for a
      // recording whose length is not known yet (no listing length, no index read across).
      // The whole player is built again for that seek -- the request below latches the
      // ones that follow, so it happens once per seek and not once per `input`.
      transcodePhase = null;
      requestTranscode(transcodeQuality, seconds, false);
    }
  }

  // What the row that was picked in the listing offers. The Play button hands the whole
  // row over rather than a URL, because the quality menu needs the frame height, the
  // length and whether the file is finished -- and reading those back out of the table
  // would tie this file to the shape of that table.
  function openRecording(file, url) {
    currentRecording = {
      path: file.path,
      url: url,
      height: typeof file.height === 'number' ? file.height : 0,
      durationMs: typeof file.durationMs === 'number' ? file.durationMs : 0,
      fragmented: file.fragmented === true,
      inProgress: file.inProgress === true
    };
    transcodeBusy = false;
    openOriginal(0);
  }

  // The recording as it was written. **Both supply sides live here**: a fragmented file
  // over MSE (it can be played while it is still being written) and a plain `<video src>`
  // for one whose `moov` is finished.
  function openOriginal(resumeAt) {
    var file = currentRecording;
    if (file === null) { return; }

    if (file.fragmented) {
      startFollow(file.url, file.path);
      resumeFollowAt(resumeAt, followGeneration);
      return;
    }

    stopFollow('');
    var element = $('player');
    element.src = file.url;
    if (0 < resumeAt) { seekWhenLoaded(element, file.url, resumeAt); }
    element.play().catch(function () { /* the browser decides; the controls remain */ });
  }

  // A position cannot be assigned before the element knows how long the media is.
  function seekWhenLoaded(element, url, seconds) {
    element.addEventListener('loadedmetadata', function () {
      // Another row may have been opened while the metadata was being read.
      if (element.getAttribute('src') !== url) { return; }
      element.currentTime = seconds;
    }, { once: true });
  }

  // Coming back from a transcode to the recording's own stream keeps the position, and
  // reaching it needs the fragment index, which arrives a moment after the first bytes.
  // **Bounded**: a file with no index plays from its beginning rather than not at all.
  var FOLLOW_RESUME_ATTEMPTS = 30;

  function resumeFollowAt(seconds, generation) {
    if (seconds <= 0) { return; }

    var attempts = 0;
    (function retry() {
      if (generation !== followGeneration || FOLLOW_RESUME_ATTEMPTS < ++attempts) { return; }
      if (followSeekTo !== null && followSeekTo(seconds)) { return; }
      setTimeout(retry, FOLLOW_INDEX_POLL_MS / 2);
    })();
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

  // Where that seek lands: this far behind the live edge. Chunked delivery is
  // continuous -- every fragment reaches the SourceBuffer as soon as the muxer
  // writes it -- so half a second of cushion keeps the decoder fed. The DASH mode
  // is delivered in one-second pieces and cannot use this number
  // (see DASH_LIVE_TARGET_SECONDS).
  var PREVIEW_LIVE_TARGET_SECONDS = 0.5;

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

  // What the server last said about this recorder's live quality: which id is
  // selected, what the source looks like and what each id would resolve to. The
  // widths and heights are **the server's arithmetic**, never recomputed here --
  // a preset means "relative to the source", and only the supply side reads the caps.
  var previewQualityState = null;

  // The id the manifest last came back with (`X-Dash-Quality`). Selected and served
  // are two different things: the mux is only rebuilt on the next sample, so for a
  // few seconds after a switch the picture is still the previous quality.
  var previewLiveQuality = null;

  function previewQualityUrl(id, tail) {
    return '/api/recorders/' + encodeURIComponent(id) + '/preview/' + tail;
  }

  // Read once per preview start. **Failure is swallowed on purpose**: the list is
  // only what the menu offers, and an older server (or a guest whose read was
  // refused) should leave the two mode entries working rather than put an error
  // over a picture that is playing.
  function loadPreviewQualities(id) {
    return getJson(previewQualityUrl(id, 'qualities')).then(function (state) {
      // The preview may have been stopped, or moved to another recorder, while
      // this was in flight; the answer belongs to the recorder that was asked for.
      if (previewTarget === id) { previewQualityState = state; }
    }).catch(function () { /* the menu stays at the two modes */ });
  }

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

    // Nothing is on offer any more: keeping the last manifest's list would have the
    // quality menu describe a stream that is not running. The quality state goes with
    // it -- it is read per recorder, and `rebuild()` depends on it being fetched again.
    dashRepresentations = [];
    previewQualityState = null;
    previewLiveQuality = null;

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
    // Not awaited: the picture must not wait on the menu's list.
    loadPreviewQualities(id);
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
      followPreview(video, PREVIEW_LAG_SECONDS, PREVIEW_LIVE_TARGET_SECONDS);
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

  // `lag` is how far behind the live edge playback has to fall before this seeks at
  // all; `target` is where the seek lands. The two modes pass different numbers
  // because they are delivered differently (see the constants) -- and the pair has to
  // match: landing closer to the edge than the stream can refill is what turns one
  // correction into a stall, a fresh correction, and another stall.
  //
  // Nothing else runs after the seek. The browser resumes on its own once the next
  // segment lands, and a handler that reacted to `waiting` would only race it.
  function followPreview(video, lag, target) {
    var ranges = video.buffered;
    if (ranges.length === 0) { return; }

    var end = ranges.end(ranges.length - 1);
    if (end - video.currentTime > lag) { video.currentTime = end - target; }
  }

  // The preview's "go live": the correction `followPreview` makes on its own, without
  // the lag threshold -- the operator asked for it, so no amount of lag is too small.
  // The landing point is the running mode's, not the smaller of the two: a DASH
  // preview parked half a second behind the live edge runs dry on the next segment.
  function resumePreviewLive() {
    var video = $('previewPlayer');
    var ranges = video.buffered;
    if (ranges.length === 0) { return; }

    var behind = $('previewMode').value === 'dash'
      ? DASH_LIVE_TARGET_SECONDS
      : PREVIEW_LIVE_TARGET_SECONDS;
    var target = ranges.end(ranges.length - 1) - behind;
    if (target <= video.currentTime) { return; }
    video.currentTime = target;
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

  // How far behind the live edge the DASH mode plays, and how far behind that it has
  // to fall before `followPreview` seeks.
  //
  // Segments are one second long and the manifest is read once a second, so half a
  // second of cushion is less than the stream needs to refill: playback runs dry
  // (`waiting`), the buffer keeps growing while it is stopped, the lag crosses the
  // threshold, and the seek drops it back exactly where it ran dry -- measured as a
  // cushion pinned at 0.50s and a `waiting` every few seconds. Two segments plus
  // jitter is a cushion the delivery can hold, and the server's ring is six seconds,
  // so aiming here stays inside what can still be fetched. The threshold is the
  // target plus one more segment, so ordinary jitter never seeks at all.
  var DASH_LIVE_TARGET_SECONDS = 2.5;
  var DASH_LAG_SECONDS = 4.5;

  // The server's word for "the encoder is running but nothing is ready yet". It
  // arrives as the `error` of a 503 and any of the three requests can meet it, so
  // none of them treats it as an answer (see `failUnlessStarting`); every other
  // non-OK answer stops the preview.
  // The C# side owns this string (Components.DashPreviewReasons.Starting) and an L1
  // test keeps the two copies equal.
  var DASH_STARTING_ERROR = 'dash preview is starting';

  var DASH_STARTING_STATUS = 'DASH: starting…';

  // Every auxiliary encoder is in use (409, `TRANSCODE_BUSY`). Treated exactly like the
  // 503 above: the recorder's mux is built on the first sample that finds a free slot, so
  // waiting is the whole of the repair -- and, unlike the 503, what has to happen first is
  // somebody else stopping.
  var DASH_BUSY_STATUS = 'DASH: busy (auxiliary encoder)';

  // What the manifest currently offers, in the order it lists them. The server
  // publishes one representation today and the first one is always the one played;
  // the list is kept so that the quality menu can be given more than one entry
  // without the parser having to change again.
  var dashRepresentations = [];

  // The direct children of `node` with the given tag name. **Not
  // `getElementsByTagName`** -- that descends, so asking an AdaptationSet for its
  // SegmentTemplate would find the ones belonging to its Representations and the
  // inheritance below would always read the child's value as the parent's.
  function childrenNamed(node, name) {
    var found = [];
    for (var child = node.firstElementChild; child !== null; child = child.nextElementSibling) {
      if (child.localName === name) { found.push(child); }
    }
    return found;
  }

  // Every Representation of every AdaptationSet, flattened. `SegmentTemplate` and
  // `codecs` are inherited from the AdaptationSet when the Representation does not
  // carry its own, which is what DASH says and what this server writes (it puts
  // `codecs` on the AdaptationSet and the template under the Representation).
  function representationsOf(periodNode) {
    var found = [];

    childrenNamed(periodNode, 'AdaptationSet').forEach(function (setNode) {
      var setTemplate = childrenNamed(setNode, 'SegmentTemplate')[0] || null;

      childrenNamed(setNode, 'Representation').forEach(function (node) {
        var template = childrenNamed(node, 'SegmentTemplate')[0] || setTemplate;
        if (template === null) { return; }

        found.push({
          id: node.getAttribute('id'),
          codecs: node.getAttribute('codecs') || setNode.getAttribute('codecs'),
          width: Number(node.getAttribute('width') || setNode.getAttribute('width')),
          height: Number(node.getAttribute('height') || setNode.getAttribute('height')),
          bandwidth: Number(node.getAttribute('bandwidth')),
          template: template
        });
      });
    });

    return found;
  }

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
    // Not awaited (see `startPreview`).
    loadPreviewQualities(id);
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
        if (response.status === 409 && body.error === TRANSCODE_BUSY) {
          status($('previewStatus'), DASH_BUSY_STATUS, false);
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
          // Which quality is **actually** being served. Read from the manifest and
          // not from the POST's answer: the switch only takes effect when the mux is
          // rebuilt, and this is the first response that comes from the new one.
          previewLiveQuality = response.headers.get('X-Dash-Quality');
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
      var offered = periodNode ? representationsOf(periodNode) : [];
      if (!periodNode || offered.length === 0) {
        fail('the manifest has no Period, AdaptationSet or SegmentTemplate');
        return;
      }

      // The first one is what plays. Re-read on every poll, because the timeline
      // this reads the new segment times from belongs to the chosen representation.
      dashRepresentations = offered;
      var templateNode = offered[0].template;

      var current = periodNode.getAttribute('id');
      if (period !== null) {
        if (current !== period) { rebuild(); return; }
        take(timelineOf(templateNode));
        return;
      }

      var timescale = Number(templateNode.getAttribute('timescale'));
      var offset = Number(templateNode.getAttribute('presentationTimeOffset'));
      var codecs = offered[0].codecs;
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
          followPreview(video, DASH_LAG_SECONDS, DASH_LIVE_TARGET_SECONDS);
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
          // The quality being served is worth more than the segment count once the
          // server names it; the count is kept for servers that do not.
          status(
            $('previewStatus'),
            'DASH: live (' + (previewLiveQuality === null ? appended : previewLiveQuality) + ')',
            false);
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

  // ---- the player shell (the controls drawn over a <video>) ----
  //
  // **The operating side only.** Nothing below feeds a SourceBuffer, asks for a byte
  // range or polls a manifest: a skip is an assignment to `currentTime`, a quality is
  // a value written to `#previewMode`. The supply side above is untouched.
  //
  // **The <video> element is wrapped, never replaced.** The E2E layer drives both
  // elements through the JS API (`play()`, `currentTime`, `playbackRate`, `buffered`)
  // and finds them by id, so the shell has to be something the element sits inside.
  //
  // The native `controls` are off (index.html): two sets of controls stacked on each
  // other is worse than either alone.

  var SHELL_SKIP_SMALL_SECONDS = 10;
  var SHELL_SKIP_LARGE_SECONDS = 30;

  // How long the bar stays up after the last pointer event, while playing.
  var SHELL_IDLE_MS = 2500;

  // Past this much lag the "go live" button is emphasised rather than merely present.
  // It has to clear the largest lag a mode reaches on purpose: the DASH preview plays
  // 2.5s behind the live edge, so a lower number would leave the button lit the whole
  // time it is behaving. The chunked mode seeks itself forward at 3s, so it never
  // reaches this. The recordings player (#player, follow mode) shares the constant:
  // after the operator seeks away its lag is no longer corrected, and the emphasis
  // starts at 5s there too.
  var SHELL_LIVE_LAG_SECONDS = 5;

  // How close to the live edge a speed **the shell raised itself** is given back.
  // A rate the operator set (or a test set directly) is never touched.
  var SHELL_SETTLE_SECONDS = 0.5;

  // The bar is repainted on the element's events, plus on this beat: the buffered end
  // and therefore the live lag move without any event being raised.
  var SHELL_TICK_MS = 250;

  var SHELL_SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];

  var SVG_NS = 'http://www.w3.org/2000/svg';

  // Whether the element is at a live edge **right now**. `caps.live` says the player
  // can be; this says it is. The follow-along player is live only while the file it
  // reads is still being written -- a LIVE badge over a finished recording is a lie,
  // and its "go live" button would seek to the last second and end the playback.
  function liveNow(video) {
    return video === $('player') ? followInProgress : true;
  }

  // What the speed control asks before it may restore 1.0 by itself. `#player`
  // follows the live edge until the user seeks (`followLive`); the preview has no
  // timeline of its own to leave, so it is always at the edge.
  function followingLiveEdge(video) {
    return video === $('player') ? followLive : true;
  }

  function shellIcon(name) {
    var svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', 'icon');
    svg.setAttribute('aria-hidden', 'true');
    var use = document.createElementNS(SVG_NS, 'use');
    use.setAttribute('href', '#' + name);
    svg.appendChild(use);
    return { svg: svg, use: use };
  }

  function pad2(value) { return value < 10 ? '0' + value : String(value); }

  function formatClock(seconds) {
    if (!isFinite(seconds) || seconds < 0) { return '--:--'; }
    var whole = Math.floor(seconds);
    var minutes = Math.floor(whole / 60);
    if (minutes < 60) { return pad2(minutes) + ':' + pad2(whole % 60); }
    return Math.floor(minutes / 60) + ':' + pad2(minutes % 60) + ':' + pad2(whole % 60);
  }

  function bufferedEndOf(video) {
    var ranges = video.buffered;
    return ranges.length === 0 ? 0 : ranges.end(ranges.length - 1);
  }

  // A position that is actually playable **for a player that cannot seek** (no
  // fragment index: a plain `<video src>`, or a file whose index could not be read).
  // A skip has to land inside something that has been buffered, because outside it
  // there is nothing for the element to decode and the position would sit there until
  // the supply side happened to reach it.
  function clampToBuffered(video, target) {
    var ranges = video.buffered;
    if (ranges.length === 0) { return video.currentTime; }

    var nearest = null;
    for (var i = 0; i < ranges.length; i++) {
      var start = ranges.start(i);
      var end = ranges.end(i);
      if (start <= target && target <= end) { return target; }

      var edge = target < start ? start : end;
      if (nearest === null || Math.abs(edge - target) < Math.abs(nearest - target)) { nearest = edge; }
    }
    return nearest;
  }

  function createShell(video, caps) {
    var wrapper = document.createElement('div');
    wrapper.className = 'player';
    wrapper.tabIndex = 0;
    video.parentNode.insertBefore(wrapper, video);
    wrapper.appendChild(video);

    // Empty in this wave. It is here so that the stacking order (picture, overlay,
    // bar) is decided once, in one place, rather than when the drawing arrives.
    var overlay = document.createElement('div');
    overlay.className = 'player-overlay';
    wrapper.appendChild(overlay);

    // Without this the wrapper is a large black rectangle whenever nothing is loaded.
    var idle = document.createElement('p');
    idle.className = 'player-idle';
    idle.textContent = video === $('player') ? 'No recording selected' : 'No preview';
    wrapper.appendChild(idle);

    var bar = document.createElement('div');
    bar.className = 'player-bar';
    wrapper.appendChild(bar);

    // Everything that is switched off while the element has no source: an element
    // with nothing loaded answers every one of these with nothing at all.
    var controls = [];
    var openMenu = null;

    // **The attribute, not `currentSrc`.** Both supply sides start by assigning
    // `video.src` and both end with `removeAttribute('src')` + `load()`, but Chromium
    // keeps the last blob: URL in `currentSrc` across that. Reading it would leave a
    // stopped player wearing a LIVE badge, a live clock and an operable bar, with the
    // idle notice never shown. The bar repaints on `emptied`, so the attribute going
    // away is seen at once.
    function hasSource() {
      return video.getAttribute('src') !== null;
    }

    // ---- pieces ----

    // `independent` marks a button that still means something with nothing loaded;
    // it is kept out of the set that goes dead.
    function iconButton(action, icon, label, independent) {
      var node = document.createElement('button');
      node.type = 'button';
      node.className = 'icon-button';
      node.dataset.action = action;
      node.setAttribute('aria-label', label);
      node.title = label;
      var drawing = shellIcon(icon);
      node.appendChild(drawing.svg);
      if (!independent) { controls.push(node); }
      return { node: node, use: drawing.use };
    }

    // The text beside the icon. Returned rather than swallowed: the speed button's
    // label is the only place the current rate is written.
    function labelled(button, label) {
      var span = document.createElement('span');
      span.className = 'player-label';
      span.textContent = label;
      button.node.appendChild(span);
      return span;
    }

    function skipButton(action, icon, delta, label) {
      var button = iconButton(action, icon, label);
      labelled(button, String(Math.abs(delta)));
      button.node.addEventListener('click', function () { skip(delta); });
      bar.appendChild(button.node);
    }

    // A menu is a button plus a panel anchored above it. Both live in one holder so
    // that the panel is positioned against its own button and not against the bar.
    function menuButton(action, icon, label, independent) {
      var holder = document.createElement('div');
      holder.className = 'player-menu-holder';

      var button = iconButton(action, icon, label, independent);
      button.node.setAttribute('aria-haspopup', 'true');
      button.node.setAttribute('aria-expanded', 'false');
      holder.appendChild(button.node);

      var menu = document.createElement('div');
      menu.className = 'player-menu';
      menu.setAttribute('role', 'menu');
      menu.hidden = true;
      holder.appendChild(menu);

      var handle = { holder: holder, button: button, menu: menu, build: null };

      button.node.addEventListener('click', function () {
        // Reached by Tab and Enter the bar may still be idle, i.e. fully transparent,
        // and the panel would open inside it unseen.
        wake();
        if (openMenu === handle) { closeMenu(); return; }
        closeMenu();
        menu.replaceChildren();
        handle.build(menu);
        menu.hidden = false;
        button.node.setAttribute('aria-expanded', 'true');
        openMenu = handle;
      });

      return handle;
    }

    function closeMenu() {
      if (openMenu === null) { return; }
      openMenu.menu.hidden = true;
      openMenu.button.node.setAttribute('aria-expanded', 'false');
      openMenu = null;
    }

    // `disabled` is for an entry the server would refuse (a Viewer picking a quality
    // that is written for every viewer of that recorder). It is drawn rather than
    // dropped: a list that silently loses entries by role reads as a broken menu.
    function menuItem(menu, label, checked, onPick, disabled) {
      var item = document.createElement('button');
      item.type = 'button';
      item.setAttribute('role', 'menuitemradio');
      item.setAttribute('aria-checked', checked ? 'true' : 'false');
      if (disabled) {
        item.disabled = true;
        item.setAttribute('aria-disabled', 'true');
      }
      item.textContent = label;
      item.addEventListener('click', function () {
        closeMenu();
        onPick();
        paint();
      });
      menu.appendChild(item);
      return item;
    }

    // ---- the bar, left to right ----

    var playButton = iconButton('play', 'i-play', 'Play');
    playButton.node.addEventListener('click', togglePlay);
    bar.appendChild(playButton.node);

    skipButton('back-30', 'i-skip-back', -SHELL_SKIP_LARGE_SECONDS, 'Back 30 seconds');
    skipButton('back-10', 'i-skip-back', -SHELL_SKIP_SMALL_SECONDS, 'Back 10 seconds');
    skipButton('forward-10', 'i-skip-forward', SHELL_SKIP_SMALL_SECONDS, 'Forward 10 seconds');
    skipButton('forward-30', 'i-skip-forward', SHELL_SKIP_LARGE_SECONDS, 'Forward 30 seconds');

    var badge = document.createElement('span');
    badge.className = 'player-badge';
    badge.textContent = 'LIVE';
    badge.hidden = true;
    bar.appendChild(badge);

    var clock = document.createElement('span');
    clock.className = 'player-time';
    bar.appendChild(clock);

    var seekWrap = document.createElement('div');
    seekWrap.className = 'player-seek';
    var band = document.createElement('div');
    band.className = 'player-buffered';
    seekWrap.appendChild(band);

    var seek = document.createElement('input');
    seek.type = 'range';
    seek.min = '0';
    seek.max = '1';
    seek.step = 'any';
    seek.value = '0';
    seek.dataset.action = 'seek';
    seek.setAttribute('aria-label', 'Position');
    controls.push(seek);
    seek.addEventListener('input', function () { seekPosition(Number(seek.value)); });
    seekWrap.appendChild(seek);
    bar.appendChild(seekWrap);

    var goLive = iconButton('live', 'i-live', 'Go to the live edge');
    goLive.node.addEventListener('click', function () { caps.onGoLive(); paint(); });
    bar.appendChild(goLive.node);

    // **Not on every player.** `caps.speed` is false for the live preview: its supply
    // side keeps the element at the live edge, so a raised rate is given back within
    // SHELL_SETTLE_SECONDS and the control reads as one that does nothing. Left
    // undrawn rather than hidden -- a hidden button still says the feature is there.
    var canSetSpeed = caps.speed !== false;
    var speedLabel = null;
    if (canSetSpeed) {
      var speedMenu = menuButton('speed', 'i-speed', 'Playback speed');
      speedLabel = labelled(speedMenu.button, '1x');
      speedMenu.build = function (menu) {
        SHELL_SPEEDS.forEach(function (rate) {
          var item = menuItem(menu, rate + 'x', video.playbackRate === rate, function () { setSpeed(rate); });
          item.dataset.speed = String(rate);
        });
      };
      bar.appendChild(speedMenu.holder);
    }

    // **Not switched off with the rest.** The `<select>` this writes is hidden, so
    // this menu is the only way left to say which stream the *next* Preview should
    // open -- disabling it while nothing is playing would take the DASH mode off the
    // page entirely.
    var qualityMenu = menuButton('quality', 'i-quality', 'Quality', true);
    qualityMenu.build = function (menu) {
      caps.qualities().forEach(function (quality) {
        var item = menuItem(
          menu, quality.label, quality.current,
          function () { caps.onQuality(quality.id); },
          quality.disabled === true);
        item.dataset.quality = quality.id;
      });
    };
    bar.appendChild(qualityMenu.holder);

    var muteButton = iconButton('mute', 'i-volume', 'Mute');
    muteButton.node.addEventListener('click', function () { video.muted = !video.muted; paint(); });
    bar.appendChild(muteButton.node);

    var volume = document.createElement('input');
    volume.type = 'range';
    volume.className = 'player-volume';
    volume.min = '0';
    volume.max = '1';
    volume.step = '0.01';
    volume.dataset.action = 'volume';
    volume.setAttribute('aria-label', 'Volume');
    volume.addEventListener('input', function () {
      video.volume = Number(volume.value);
      video.muted = Number(volume.value) === 0;
    });
    controls.push(volume);
    bar.appendChild(volume);

    var fullscreen = iconButton('fullscreen', 'i-fullscreen', 'Full screen');
    fullscreen.node.addEventListener('click', toggleFullscreen);
    bar.appendChild(fullscreen.node);

    // ---- operations ----

    function togglePlay() {
      if (video.paused) {
        video.play().catch(function () { /* the browser decides; the bar stays */ });
      } else {
        video.pause();
      }
    }

    // Whether a position outside what is buffered can be reached at all. False until
    // the supply side says otherwise (`setSeekable`) -- a control that answers a drag
    // by snapping back reads as broken rather than as unavailable, so until then the
    // seek bar is shown and not operated.
    var seekable = caps.seekable === true;
    var seekLimit = 0;

    // `null` puts the bar back to the state it has with no index; a number both turns
    // seeking on and says how far the media reaches (a recording still being written
    // grows, so this arrives again on every index update).
    function setSeekable(limit) {
      seekable = limit !== null;
      seekLimit = seekable ? limit : 0;
      paint();
    }

    // Where the media comes from is the supply side's business: it is the one that can
    // reach a position it has not fetched (`caps.onSeek`). Without that hook the
    // element is simply told where to go, which only works inside what is buffered.
    function seekPosition(target) {
      if (!seekable || !hasSource()) { return; }
      if (caps.onSeek) { caps.onSeek(target); return; }
      video.currentTime = target;
    }

    function skip(delta) {
      if (!hasSource()) { return; }
      var target = video.currentTime + delta;
      if (seekable) {
        var limit = 0 < seekLimit
          ? seekLimit
          : (isFinite(video.duration) ? video.duration : bufferedEndOf(video));
        seekPosition(Math.max(0, Math.min(limit, target)));
        return;
      }
      video.currentTime = clampToBuffered(video, target);
    }

    // True while the rate above 1.0 on the element is one this shell put there. Only
    // that one is taken back: a rate the operator chose -- or that a test wrote
    // straight onto the element -- is theirs to keep.
    var raised = false;

    function setSpeed(rate) {
      video.playbackRate = rate;
      // `liveNow` as well: a finished recording has a buffered end too, and dropping
      // back to 1.0 when the playback reaches it is not something anyone asked for.
      raised = caps.live && rate > 1 && followingLiveEdge(video) && liveNow(video);
    }

    function settleSpeed() {
      if (!raised) { return; }
      if (bufferedEndOf(video) - video.currentTime >= SHELL_SETTLE_SECONDS) { return; }
      raised = false;
      video.playbackRate = 1;
    }

    function toggleFullscreen() {
      if (document.fullscreenElement === wrapper) {
        document.exitFullscreen();
        return;
      }
      if (wrapper.requestFullscreen) {
        // Older implementations return undefined rather than a promise, and there
        // `.catch` is itself the error.
        Promise.resolve(wrapper.requestFullscreen())
          .catch(function () { /* refused; nothing to repair */ });
        return;
      }
      // iOS has no element fullscreen: the <video> is the only thing that can go
      // full screen there, and it brings its own native controls when it does.
      if (video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); }
    }

    // ---- painting ----

    var bands = [];

    function paintBand(max) {
      var ranges = video.buffered;
      if (bands.length !== ranges.length) {
        band.replaceChildren();
        bands = [];
        for (var i = 0; i < ranges.length; i++) {
          var span = document.createElement('span');
          band.appendChild(span);
          bands.push(span);
        }
      }
      for (var j = 0; j < bands.length; j++) {
        bands[j].style.left = (100 * ranges.start(j) / max) + '%';
        bands[j].style.width = (100 * (ranges.end(j) - ranges.start(j)) / max) + '%';
      }
    }

    function paint() {
      var source = hasSource();
      for (var i = 0; i < controls.length; i++) { controls[i].disabled = !source; }
      // Shown, not operated, until there is a way to reach a position outside what has
      // been buffered.
      seek.disabled = !source || !seekable;
      seek.tabIndex = seekable ? 0 : -1;
      idle.hidden = source;

      var playing = !video.paused && !video.ended;
      playButton.use.setAttribute('href', playing ? '#i-pause' : '#i-play');
      playButton.node.setAttribute('aria-label', playing ? 'Pause' : 'Play');

      var end = bufferedEndOf(video);
      // The index's length first: a recording still being written has `duration`
      // Infinity, and the buffered end is only the part that has been fetched.
      var total = seekable && 0 < seekLimit
        ? seekLimit
        : (isFinite(video.duration) && video.duration > 0 ? video.duration : end);
      var live = caps.live && source && liveNow(video);
      var lag = Math.max(0, end - video.currentTime);

      badge.hidden = !live;
      goLive.node.hidden = !live;
      goLive.node.classList.toggle('is-behind', live && lag > SHELL_LIVE_LAG_SECONDS);

      clock.textContent = live
        ? formatClock(video.currentTime) + ' / ' + formatClock(total) + ' (-' + lag.toFixed(1) + 's)'
        : formatClock(video.currentTime) + ' / ' + formatClock(total);

      var max = total > 0 ? total : 1;
      seek.max = String(max);
      // Not while it is being dragged: writing the value under the pointer fights it.
      if (document.activeElement !== seek) { seek.value = String(Math.min(video.currentTime, max)); }
      paintBand(max);

      var muted = video.muted || video.volume === 0;
      muteButton.use.setAttribute('href', muted ? '#i-volume-mute' : '#i-volume');
      muteButton.node.setAttribute('aria-label', muted ? 'Unmute' : 'Mute');
      if (document.activeElement !== volume) { volume.value = String(muted ? 0 : video.volume); }

      if (speedLabel !== null) { speedLabel.textContent = video.playbackRate + 'x'; }

      // One quality is not a choice: the menu is only drawn when there is something
      // to pick between.
      qualityMenu.holder.hidden = caps.qualities().length < 2;

      fullscreen.use.setAttribute(
        'href', document.fullscreenElement === wrapper ? '#i-fullscreen-exit' : '#i-fullscreen');
    }

    // ---- the bar hiding itself ----

    var lastPointer = Date.now();

    function wake() {
      lastPointer = Date.now();
      bar.classList.remove('is-idle');
    }

    // **Hover belongs to the mouse alone.** A touch raises `pointerenter` and
    // `pointermove` before its own `pointerdown`, so waking here would show the bar
    // first and let the tap below hide it again: a hidden bar could never be tapped
    // back up.
    function wakeFromHover(event) {
      if (event.pointerType === 'touch') { return; }
      wake();
    }

    wrapper.addEventListener('pointermove', wakeFromHover);
    wrapper.addEventListener('pointerenter', wakeFromHover);
    wrapper.addEventListener('pointerdown', function (event) {
      // Touch has no hover, so there is nothing to bring the bar back: tapping the
      // picture is what shows and hides it.
      if (event.pointerType === 'touch' && event.target === video) {
        if (bar.classList.contains('is-idle')) { wake(); } else { bar.classList.add('is-idle'); }
        return;
      }
      wake();
    });

    // Reached with the keyboard rather than the pointer: a bar that is transparent
    // cannot be operated by the focus that just landed in it.
    wrapper.addEventListener('focusin', wake);

    // Paused is when the controls are wanted, and the beat below only ever hides. A
    // bar that faded out while playing has to come back when the playback stops.
    video.addEventListener('pause', wake);
    video.addEventListener('ended', wake);

    // ---- keys ----

    wrapper.addEventListener('keydown', function (event) {
      var tag = event.target && event.target.tagName;
      // A control that reads keys of its own keeps them (the sliders and, for Space
      // and Enter, the bar's own buttons -- otherwise one press both clicks the
      // button and does whatever this handler makes of it).
      if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') { return; }
      if (tag === 'BUTTON' && (event.key === ' ' || event.key === 'Enter')) { return; }
      if (event.altKey || event.ctrlKey || event.metaKey) { return; }

      var large = event.shiftKey ? SHELL_SKIP_LARGE_SECONDS : SHELL_SKIP_SMALL_SECONDS;
      var handled = true;

      switch (event.key) {
        case ' ': case 'k': case 'K': togglePlay(); break;
        case 'j': case 'J': case 'ArrowLeft': skip(-large); break;
        case 'l': case 'L': case 'ArrowRight': skip(large); break;
        case 'f': case 'F': toggleFullscreen(); break;
        case 'm': case 'M': video.muted = !video.muted; break;
        case ',': handled = stepSpeed(-1); break;
        case '.': handled = stepSpeed(1); break;
        case 'Escape': closeMenu(); break;
        default: handled = false;
      }

      if (handled) {
        event.preventDefault();
        paint();
      }
    });

    // Answers whether the key was acted on: where the menu is not drawn the shortcut
    // is not the shell's either, and the press is left to the page.
    function stepSpeed(direction) {
      if (!canSetSpeed) { return false; }
      var index = SHELL_SPEEDS.indexOf(video.playbackRate);
      if (index < 0) { index = SHELL_SPEEDS.indexOf(1); }
      setSpeed(SHELL_SPEEDS[Math.max(0, Math.min(SHELL_SPEEDS.length - 1, index + direction))]);
      return true;
    }

    // A click anywhere else closes the open menu. On the document, because "anywhere
    // else" includes the rest of the page.
    document.addEventListener('click', function (event) {
      if (openMenu !== null && !openMenu.holder.contains(event.target)) { closeMenu(); }
    });

    // ---- what the bar listens to ----

    [
      'loadstart', 'loadedmetadata', 'durationchange', 'timeupdate', 'progress',
      'play', 'pause', 'ended', 'error', 'emptied', 'ratechange', 'volumechange',
      'seeking', 'seeked', 'waiting', 'canplay'
    ].forEach(function (name) {
      video.addEventListener(name, paint);
    });

    // A rate that is no longer above 1.0 cannot be one this shell is holding up.
    video.addEventListener('ratechange', function () {
      if (video.playbackRate <= 1) { raised = false; }
    });

    document.addEventListener('fullscreenchange', paint);

    setInterval(function () {
      settleSpeed();
      if (!video.paused && openMenu === null && SHELL_IDLE_MS < Date.now() - lastPointer) {
        bar.classList.add('is-idle');
      }
      paint();
    }, SHELL_TICK_MS);

    paint();
    return {
      wrapper: wrapper, overlay: overlay, bar: bar, paint: paint, setSeekable: setSeekable
    };
  }

  // ---- what each of the two players can do ----

  /** The <option> text for one of the two modes (the selector owns the wording). */
  function previewModeLabel(value) {
    var select = $('previewMode');
    for (var i = 0; i < select.options.length; i++) {
      if (select.options[i].value === value) { return select.options[i].textContent; }
    }
    return value;
  }

  // The menu is "the recording's own stream" followed by everything the server says
  // it can re-encode to. **There is no `dash` entry**: the DASH mode is not one
  // quality but a family of them, and the id a menu entry carries is the id the
  // server knows (`1080p`…`360p`, `custom`).
  //
  // Until the list has been read -- no preview running, or the read was refused --
  // the two modes are offered as before, with `custom` standing for the DASH one.
  function previewQualities() {
    var select = $('previewMode');
    var offered = [{
      id: 'recording',
      label: previewModeLabel('recording'),
      current: select.value === 'recording'
    }];

    if (previewQualityState === null) {
      offered.push({ id: 'custom', label: previewModeLabel('dash'), current: select.value === 'dash' });
      return offered;
    }

    // A Viewer may switch modes but may not write the selection, which is shared by
    // every viewer of that recorder: everything but the current entry is unpickable.
    var canWrite = allows('operator');
    previewQualityState.qualities.forEach(function (quality) {
      offered.push({
        id: quality.id,
        label: quality.id === 'custom'
          ? 'Custom (' + quality.width + '×' + quality.height + ' ' + quality.fps + ' fps)'
          : quality.label,
        current: select.value === 'dash' && previewQualityState.current === quality.id,
        disabled: !canWrite && previewQualityState.current !== quality.id
      });
    });

    return offered;
  }

  // Writing the selector's value is not enough: the handler that stops the running
  // preview and starts the other mode is its `change`, and a value set from script
  // does not raise it.
  //
  // **The POST comes first, the mode switch second.** The other order would build
  // one mux at the old quality and throw it away a moment later, which costs a
  // rebuild (2 to 4 seconds of black) for nothing.
  function choosePreviewQuality(id) {
    var select = $('previewMode');
    if (id === 'recording') {
      select.value = id;
      select.dispatchEvent(new Event('change'));
      return;
    }

    // The preview may be stopped, or moved to another recorder, while the POST is in
    // flight; both the answer and the mode switch that follows it belong to the
    // recorder that was asked for.
    var target = previewTarget;
    var needsPost = target !== null && previewQualityState !== null
      && previewQualityState.current !== id && allows('operator');

    var written = needsPost
      ? send('POST', previewQualityUrl(target, 'quality'), { id: id })
        .then(function (state) {
          if (previewTarget === target) { previewQualityState = state; }
        })
      : Promise.resolve();

    written.then(function () {
      if (previewTarget !== target) { return; }
      if (select.value !== 'dash') {
        select.value = 'dash';
        select.dispatchEvent(new Event('change'));
      }
    }).catch(function (error) {
      status($('previewStatus'), error.message, true);
    });
  }

  // "The recording as it was written", followed by every preset this machine could
  // re-encode it to. **The first entry is always offered and never uses an encoder slot**:
  // whatever else is refused, the bytes on disk can be served.
  //
  // The presets appear only for a finished recording. A file that is still being written
  // has no length the converter could rely on -- `filesrc` decides where the end is when
  // it opens -- and the server refuses it for that reason.
  function recordingQualities() {
    var offered = [{ id: 'original', label: 'Original', current: !transcodeActive }];

    var capabilities = core.state.capabilities;
    if (capabilities === null || capabilities.transcode !== true
        || currentRecording === null || currentRecording.inProgress) {
      return offered;
    }

    // **A refusal is drawn, not dropped.** A menu that loses its entries whenever the
    // slots are taken reads as broken; one that greys them out says what is going on. The
    // transcode that is running is exempt: switching it keeps the slot it already holds
    // (the server sees the same session id and hands the lease over).
    var busy = (transcodeBusy || auxiliaryEncodersFree === 0) && !transcodeActive;

    transcodePresetsFor(currentRecording.height).forEach(function (preset) {
      offered.push({
        id: preset.id,
        label: busy ? preset.label + ' (busy)' : preset.label,
        current: transcodeActive && transcodeQuality === preset.id,
        disabled: busy
      });
    });

    return offered;
  }

  // Which presets a frame of this height can be asked for: the ones that are not larger
  // than it, because the server never scales a recording up. A height it does not know
  // (an older sidecar) offers all of them -- the server resolves each against the real
  // caps anyway, so the worst case is a menu entry that lands on the source's own size.
  function transcodePresetsFor(height) {
    if (height <= 0) { return TRANSCODE_PRESETS; }

    var cap = height - (height % 2);
    var offered = TRANSCODE_PRESETS.filter(function (preset) { return preset.height <= cap; });
    // Nothing fits: the smallest preset is still offered, which is what the server does
    // for a source below every one of them.
    return 0 < offered.length ? offered : [TRANSCODE_PRESETS[TRANSCODE_PRESETS.length - 1]];
  }

  // The position is carried across the switch in both directions: a quality menu that
  // sends the playback back to the beginning is one nobody uses twice.
  function chooseRecordingQuality(id) {
    rememberRecordingLength();
    var at = $('player').currentTime;
    if (id === 'original') {
      openOriginal(at);
      return;
    }
    // Picking a quality is asking to watch it, the same as opening a row: the playback
    // that is built for it starts. (A seek is the other case -- see `restartTranscodeAt`.)
    transcodeResume = true;
    requestTranscode(id, at, false);
  }

  // **The listing has no length for a fragmented recording** (`moov` says 0 and is never
  // rewritten), so the only place it exists is the fragment index -- which is released along
  // with the follow. Read across before the switch, it is what gives the transcoded playback
  // a seek bar; without it the element refuses to seek at all.
  function rememberRecordingLength() {
    if (currentRecording === null || 0 < currentRecording.durationMs) { return; }
    if (followIndex === null || !(0 < followIndex.timescale)) { return; }

    currentRecording.durationMs = 1000 * followIndex.totalDuration / followIndex.timescale;
  }

  // How many slots the server last reported free (SSE `state`). A slot coming back is the
  // only thing that clears a refusal: nothing this page does can free one.
  function onAuxiliaryEncoders(free) {
    if (typeof free !== 'number') { return; }
    auxiliaryEncodersFree = free;
    if (0 < free) { transcodeBusy = false; }
  }

  // Both elements are shelled here rather than in app.js: the capabilities are this
  // file's own state (whether the followed file is still growing, what the preview
  // selector offers, how each of the two rejoins its live edge).
  var playerShell = createShell($('player'), {
    live: true,
    // Turned on by `setSeekable` once the fragment index for the open file has been
    // read. A plain `<video src>` recording never gets one and keeps the clamped skip.
    seekable: false,
    speed: true,
    qualities: recordingQualities,
    onQuality: chooseRecordingQuality,
    onGoLive: resumeFollow,
    onSeek: function (seconds) {
      if (transcodeActive) { seekTranscode(seconds); return; }
      if (followSeekTo !== null) { followSeekTo(seconds); }
    }
  });

  followSetSeekable = playerShell.setSeekable;

  createShell($('previewPlayer'), {
    live: true,
    seekable: false,
    // The supply side holds this element at the live edge, so a rate above 1.0 is
    // taken back within half a second: the menu could only ever look broken.
    speed: false,
    qualities: previewQualities,
    onQuality: choosePreviewQuality,
    onGoLive: resumePreviewLive
  });

  // ---- listeners on the two <video> elements ----
  //
  // The elements outlive every playback and every connection, so app.js attaches
  // each of these once. The bodies are here because the state they read -- the
  // failure hooks, the follow flags, the recorder a preview belongs to -- is this
  // file's.

  // A seek the user made ends the live-edge following. A correction this file made
  // is recognised by the position it asked for -- within FOLLOW_SEEK_MATCH_SECONDS,
  // because the element has usually moved on by the time this runs -- and only
  // that one is forgiven.
  function onPlayerSeeking() {
    var video = $('player');

    // A transcode reads forward from the position it was started at, so a position it has
    // not reached is a new request rather than a wait. The moves this file makes itself
    // (putting the element where the media starts) are recognised the same way the
    // follow's corrections are, and are not restarts.
    if (transcodeActive) {
      var ours = transcodeSeekTarget !== null
        && Math.abs(video.currentTime - transcodeSeekTarget) < FOLLOW_SEEK_MATCH_SECONDS;
      transcodeSeekTarget = null;
      if (ours || insideBuffered(video, video.currentTime)) { return; }
      restartTranscodeAt(video.currentTime);
      return;
    }

    if (followSeekTarget !== null
        && Math.abs(video.currentTime - followSeekTarget) < FOLLOW_SEEK_MATCH_SECONDS) {
      followSeekTarget = null;
      return;
    }
    followSeekTarget = null;
    followLive = false;

    // A position outside what is buffered has nothing to decode. With an index the
    // supply side can be restarted at the fragment that carries it; without one the
    // element waits where it is, as before. **The re-entry flag is needed**: the
    // restart assigns `currentTime`, which raises this very event.
    if (followReseeking || followSeekTo === null || insideBuffered(video, video.currentTime)) {
      return;
    }

    followReseeking = true;
    try {
      followSeekTo(video.currentTime);
    } finally {
      followReseeking = false;
    }
  }

  // A decode failure is otherwise silent: the picture stops and the polling keeps
  // running.
  function onPlayerError() {
    var media = $('player').error;
    if (followOnFailure !== null) {
      followOnFailure('playback error' + (media ? ' (code ' + media.code + ')' : ''));
    }
  }

  // A decode failure is otherwise silent: the picture stops and the network keeps
  // running.
  function onPreviewError() {
    var media = $('previewPlayer').error;
    if (previewOnFailure !== null) {
      previewOnFailure('playback error' + (media ? ' (code ' + media.code + ')' : ''));
    }
  }

  // Changing the mode while a preview runs reopens the same recorder in the new one.
  // Nothing can be carried over: the two modes differ in codec parameters, in how the
  // timeline is built and in how the server accounts for the viewer.
  function onPreviewModeChange() {
    if (previewTarget !== null) { startSelectedPreview(previewTarget); }
  }

  // **Leaving the page gives the encoder slot back.** The router hides a page, it never
  // takes one down, so a transcode would otherwise keep converting -- and keep its slot --
  // for as long as the tab is open. Only the transcode is stopped: a playback the operator
  // left running is theirs, and the recording's own bytes cost the server nothing.
  //
  // app-core.js owns the router and is loaded first, so the page's `hidden` is already up
  // to date by the time this runs.
  //
  // **A request that is still on the wire counts as running.** `transcodeActive` is only
  // set once the answer has been installed, and the server holds the slot from the moment
  // it starts converting -- so leaving during 'requesting' would let the answer arrive on
  // a page nobody is looking at and keep the slot for the life of the tab. `stopTranscode`
  // is what stops it: the attempt it bumps is no longer the newest, so the answer is
  // dropped unread and its connection aborted.
  window.addEventListener('hashchange', function () {
    if ($('pageRecordings').hidden && (transcodeActive || transcodePhase === 'requesting')) {
      stopTranscode();
      status($('playerStatus'), '', false);
    }
  });

  PRA.player = {
    createShell: createShell,
    // What the manifest last offered. One entry today; the quality menu reads it once
    // the server publishes more than one.
    representations: function () { return dashRepresentations; },
    // What the server last answered about the live quality (null until a preview has
    // been started). Exposed for the E2E layer, which must not recompute the
    // resolution arithmetic to check it -- the server's own numbers are the expectation.
    previewQualityState: function () { return previewQualityState; },
    startFollow: startFollow,
    stopFollow: stopFollow,
    // How a row of the listing is played. **The whole row is handed over**, because which
    // qualities the menu may offer is decided from its height, its length and whether it
    // is finished -- and reading those back out of the table would tie this file to the
    // shape of that table.
    openRecording: openRecording,
    // How many auxiliary encoder slots the server last reported free (SSE `state`).
    onAuxiliaryEncoders: onAuxiliaryEncoders,
    // What is being transcoded, for the E2E layer. `active` is false whenever the
    // recording's own stream is playing, which is every case on a machine with no
    // hardware H.264 decoder.
    transcodeState: function () {
      return {
        active: transcodeActive,
        quality: transcodeQuality,
        session: transcodeSession,
        start: transcodeStart
      };
    },
    // Arbitrary seeking within the recording being followed. False when there is no
    // index to answer from.
    seekTo: function (seconds) { return followSeekTo !== null && followSeekTo(seconds); },
    startSelectedPreview: startSelectedPreview,
    stopPreview: stopPreview,
    onPlayerSeeking: onPlayerSeeking,
    onPlayerError: onPlayerError,
    onPreviewError: onPreviewError,
    onPreviewModeChange: onPreviewModeChange
  };
})();
