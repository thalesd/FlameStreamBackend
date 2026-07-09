// FlameStream custom Cast receiver — self-driven playback edition (2026-07-03).
//
// WHY THIS EXISTS (see also the /api/castlog relay on the backend):
// This TV's CAF PlayerManager fails EVERY load — our HLS, Google's own public HLS
// sample, and plain direct MP4 alike — with LOAD_FAILED (905) ~25ms after
// PLAYER_LOADING and zero network activity. Meanwhile raw <video> + MSE + hls.js on
// this same page plays those exact streams flawlessly (probe: manifest parsed,
// segments fetched, playback reached in ~4s). So the Cast SDK here does session
// management ONLY, and playback is ours:
//
//   sender  --urn:x-cast:flamestream-->  this page  --hls.js/MSE-->  <video>
//
// Message protocol (JSON over the custom namespace):
//   sender → receiver:
//     {type:'load', url, title?, duration?, startTime?, tracks?: [{id,url,name,lang}], activeTrackId?}
//     {type:'play'} {type:'pause'} {type:'seek', time}          (time = original-file seconds)
//     {type:'setVolume', level} {type:'setMuted', muted}
//     {type:'setTrack', id}                                      (null id = subtitles off)
//   receiver → sender (broadcast ~1s + on change):
//     {type:'status', t, dur, paused, trackId}
(function () {
  const NS = 'urn:x-cast:flamestream';
  const context = cast.framework.CastReceiverContext.getInstance();

  const video      = document.getElementById('player');
  const splash     = document.getElementById('splash');
  const titleBar   = document.getElementById('title-bar');
  const stateBadge = document.getElementById('state-badge');
  const scrub      = document.getElementById('scrub');
  const scrubThumb = document.getElementById('scrub-thumb');
  const scrubBar   = document.getElementById('scrub-bar-fill');
  const scrubTime  = document.getElementById('scrub-time');
  // Fade the preview only once its frame has actually loaded; hide a failed one.
  scrubThumb.addEventListener('load',  () => { scrubThumb.style.opacity = '1'; });
  scrubThumb.addEventListener('error', () => { scrubThumb.style.opacity = '0'; });

  // ── Debug overlay + backend log relay ───────────────────────────────────────
  // DEBUG toggles ONLY the on-TV overlay. dlog() always goes to the console and to
  // POST /api/castlog (LAN-readable ring buffer) — that relay is how this receiver
  // gets debugged at all (the TV's remote-DevTools port is unreachable), so it stays
  // on even for normal viewing; it's a handful of tiny same-origin requests.
  const DEBUG = false;
  let logEl = null;
  const logLines = [];
  function renderLog() {
    if (!DEBUG) return;
    if (!logEl && document.body) {
      logEl = document.createElement('div');
      logEl.style.cssText =
        'position:fixed;left:8px;bottom:8px;max-width:94vw;max-height:40vh;overflow:hidden;' +
        'font:13px/1.4 monospace;color:#8f8;background:rgba(0,0,0,.6);padding:6px 9px;' +
        'border-radius:4px;white-space:pre-wrap;word-break:break-all;z-index:99999;pointer-events:none;';
      document.body.appendChild(logEl);
    }
    if (logEl) logEl.textContent = logLines.join('\n');
  }
  function dlog(msg) {
    console.log('[FlameStream receiver] ' + msg);
    try { fetch('/api/castlog', { method: 'POST', body: msg }); } catch (e) {}
    if (!DEBUG) return;
    logLines.push(msg);
    while (logLines.length > 14) logLines.shift();
    renderLog();
  }
  window.addEventListener('error', (e) => {
    // With crossorigin on the SDK script (see receiver.html), e.error carries the real
    // Error incl. stack — the first frames pinpoint what inside CAF is throwing.
    let extra = '';
    if (e.error && e.error.stack) extra = ' :: ' + String(e.error.stack).split('\n').slice(0, 3).join(' | ');
    dlog('JS ERROR: ' + (e.message || '?') + ' @' + (e.filename || '?') + ':' + (e.lineno || '?') + ':' + (e.colno || '?') + extra);
  });
  window.addEventListener('unhandledrejection', (e) => {
    let r = ''; try { r = JSON.stringify(e.reason); } catch (err) { r = String(e.reason); }
    dlog('UNHANDLED REJECTION: ' + r);
  });

  // ── Playback state ──────────────────────────────────────────────────────────
  let hls = null;
  let currentUrl = null;        // base stream URL (no ?start)
  let totalDuration = 0;        // original-file duration, from the sender's library metadata
  let startOffset = 0;          // absolute time of the current manifest's local 0
  let tracks = [];              // subtitle tracks offered by the sender
  let activeTrackId = null;
  let trackEl = null;
  let statusTimer = null;
  let playerManager = null;     // CAF PlayerManager, used only as the remote-control surface

  // ── D-pad scrub-to-confirm state (#130) ─────────────────────────────────────
  // The remote is a D-pad, so seeking is a deliberate scrub: Left/Right adjust a target
  // (media paused, thumbnail previewing it), OK commits, Back cancels — mirroring the web
  // app. Nothing is sought until commit.
  let seeking = false;
  let seekTarget = 0;                 // absolute (original-file) seconds being previewed
  let wasPlayingBeforeSeek = false;   // restore this play state on commit/cancel
  let thumbBaseUrl = null;            // /api/thumb/<path> for this title, from the load msg
  let thumbDebounce = null;
  const SEEK_STEP = 10;               // seconds per Left/Right press
  const THUMB_INTERVAL = 10;          // preview grid; matches the backend's bucket size

  function stripQuery(url) {
    const i = url.indexOf('?');
    return i === -1 ? url : url.substring(0, i);
  }

  // The backend reports the absolute time of the manifest's local 0 in this header;
  // it is the served segment's start, not necessarily the requested seek target.
  // Mirrors applyStartOffsetHeader in the web app.
  function applyStartOffsetHeader(data) {
    const raw = data && data.networkDetails && data.networkDetails.getResponseHeader &&
                data.networkDetails.getResponseHeader('X-Hls-Start-Offset');
    const s = raw != null ? parseFloat(raw) : NaN;
    if (isNaN(s) || s === startOffset) return;
    startOffset = s;
    dlog('start offset → ' + s.toFixed(2) + 's');
    if (activeTrackId != null) attachSubtitleTrack(activeTrackId); // re-shift cues
  }

  // Native cue matching compares against raw video.currentTime, which resets to ~0 on
  // every (re)load — the backend rewrites cue times by ?shift= to compensate.
  // Mirrors attachSubtitleTrack in the web app.
  function attachSubtitleTrack(id) {
    if (trackEl) { try { trackEl.remove(); } catch (e) {} trackEl = null; }
    const t = tracks.find((x) => x.id === id);
    if (!t) return;
    const el = document.createElement('track');
    el.kind = 'subtitles';
    el.label = t.name || 'Legendas';
    el.srclang = t.lang || 'pt';
    el.src = startOffset > 0
      ? t.url + (t.url.indexOf('?') !== -1 ? '&' : '?') + 'shift=' + startOffset
      : t.url;
    video.appendChild(el);
    trackEl = el;
    el.track.mode = 'showing';
    dlog('subtitle track ' + id + ' attached (shift=' + startOffset.toFixed(1) + ')');
  }

  function destroyHls() {
    if (hls) { try { hls.destroy(); } catch (e) {} hls = null; }
    if (trackEl) { try { trackEl.remove(); } catch (e) {} trackEl = null; }
  }

  // Loads the stream at an absolute start time. Handles both fresh loads and
  // out-of-buffer seeks (mirrors loadLocal + reloadFrom in the web app).
  // resumePlaying === false keeps the video paused once ready (a commit-seek made while
  // the media was paused); anything else autoplays, as normal loads should.
  function loadAt(absoluteStart, resumePlaying) {
    destroyHls();
    startOffset = absoluteStart; // provisional until X-Hls-Start-Offset corrects it
    const shouldPlay = resumePlaying !== false;

    const url = absoluteStart > 0 ? currentUrl + '?start=' + absoluteStart : currentUrl;
    dlog('loadAt ' + absoluteStart.toFixed(1) + 's' + (shouldPlay ? '' : ' (paused)'));

    if (currentUrl.indexOf('.m3u8') === -1) {
      // Direct file (progressive MP4) — native playback, native seeking.
      video.src = currentUrl;
      video.currentTime = absoluteStart;
      if (shouldPlay) video.play().catch(() => {}); else video.pause();
      if (activeTrackId != null) attachSubtitleTrack(activeTrackId);
      return;
    }

    // startPosition: 0 — in-progress transcodes are EVENT playlists that hls.js would
    // otherwise treat as live and start at the encode tip (same as the web app).
    // Buffer limits sized for TV media stacks: their MSE SourceBuffer quotas are far
    // smaller than desktop Chrome's (observed: appends start failing around ~35MB with
    // our ~7MB segments, causing steady retry churn). Keep the forward buffer modest
    // and let the back buffer be reclaimed aggressively.
    hls = new Hls({
      enableWorker: true,
      startPosition: 0,
      maxBufferLength: 20,
      maxMaxBufferLength: 30,
      maxBufferSize: 30 * 1000 * 1000,
      backBufferLength: 30,
    });
    let lastNonFatal = '';
    hls.on(Hls.Events.ERROR, (_e, d) => {
      if (d.fatal) return; // fatal handler below
      if (d.details !== lastNonFatal) {   // log each distinct issue once, not per retry
        lastNonFatal = d.details;
        dlog('hls non-fatal: ' + d.details);
      }
    });
    hls.on(Hls.Events.MANIFEST_LOADED, (_e, d) => applyStartOffsetHeader(d));
    hls.on(Hls.Events.LEVEL_LOADED, (_e, d) => applyStartOffsetHeader(d));
    hls.on(Hls.Events.MANIFEST_PARSED, () => {
      if (shouldPlay) video.play().catch(() => {}); else video.pause();
      if (activeTrackId != null) attachSubtitleTrack(activeTrackId);
    });
    hls.on(Hls.Events.ERROR, (_e, d) => {
      if (!d.fatal) return;
      dlog('hls FATAL ' + d.type + ' / ' + d.details);
      if (d.type === Hls.ErrorTypes.NETWORK_ERROR) {
        loadAt(video.currentTime + startOffset); // reload where we were
      } else if (d.type === Hls.ErrorTypes.MEDIA_ERROR && hls) {
        hls.recoverMediaError();
      }
    });
    hls.attachMedia(video);
    hls.on(Hls.Events.MEDIA_ATTACHED, () => hls.loadSource(url));
  }

  // Seek to an absolute (original-file) time. Direct files seek natively anywhere;
  // HLS reloads when outside the buffer so the backend can start a seek transcode
  // if needed (mirrors the web app's seek()/reloadFrom()).
  // resumePlaying (optional): true → play after seeking, false → stay paused; omitted →
  // leave the current play state untouched (the pre-existing CAF/custom-channel behavior).
  function doSeek(target, resumePlaying) {
    if (typeof target !== 'number' || isNaN(target) || !currentUrl) return;
    target = Math.max(0, totalDuration > 0 ? Math.min(target, totalDuration - 2) : target);
    const local = target - startOffset;
    let buffered = false;
    for (let i = 0; i < video.buffered.length; i++) {
      if (local >= video.buffered.start(i) - 1 && local <= video.buffered.end(i)) { buffered = true; break; }
    }
    if (buffered || currentUrl.indexOf('.m3u8') === -1) {
      video.currentTime = currentUrl.indexOf('.m3u8') === -1 ? target : local;
      if (resumePlaying === true) video.play().catch(() => {});
      else if (resumePlaying === false) video.pause();
    } else {
      loadAt(target, resumePlaying);
    }
    sendStatus();
  }

  // Current absolute (original-file) playback position.
  function absTime() { return video.currentTime + startOffset; }

  function togglePlay() {
    if (video.paused) { video.play().catch(() => {}); showBadge('▶'); }
    else { video.pause(); showBadge('⏸ Pausado'); }
  }

  // Relative seek used by every control surface (remote keys, CAF session, sender).
  function seekBy(delta) {
    doSeek(absTime() + delta);
    showBadge(delta >= 0 ? '⏩ +' + delta + 's' : '⏪ ' + delta + 's');
  }

  function changeVolume(delta) {
    video.muted = false;
    video.volume = Math.max(0, Math.min(1, video.volume + delta));
    showBadge('🔊 ' + Math.round(video.volume * 100) + '%');
  }

  function formatTime(s) {
    if (!s || isNaN(s) || s < 0) s = 0;
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = Math.floor(s % 60);
    const mm = (m < 10 && h > 0 ? '0' : '') + m, ss = (sec < 10 ? '0' : '') + sec;
    return h > 0 ? h + ':' + mm + ':' + ss : m + ':' + ss;
  }

  // ── Scrub-to-confirm (#130) ─────────────────────────────────────────────────
  // Enter scrub mode: pause and remember the play state so it can be restored on
  // commit/cancel. The media element keeps showing its paused frame; the overlay
  // previews the target.
  function enterSeek() {
    seeking = true;
    wasPlayingBeforeSeek = !video.paused;
    video.pause();
    seekTarget = absTime();
    stateBadge.classList.remove('visible'); // the scrub overlay replaces the pause badge
    scrub.classList.remove('hidden');
  }

  // Left/Right nudge the target (entering scrub mode on the first press). No seek yet.
  function adjustSeek(delta) {
    if (!seeking) enterSeek();
    const max = totalDuration > 0 ? totalDuration : seekTarget + delta;
    seekTarget = Math.max(0, Math.min(seekTarget + delta, max));
    renderScrub();
  }

  function renderScrub() {
    scrubTime.textContent = formatTime(seekTarget) + (totalDuration > 0 ? ' / ' + formatTime(totalDuration) : '');
    if (totalDuration > 0) scrubBar.style.width = (seekTarget / totalDuration * 100) + '%';
    if (!thumbBaseUrl) return;
    // Quantize to the backend's preview grid so we reuse cached frames (one request per
    // bucket), and debounce so rapid nudges don't fire a request per keypress.
    const bucket = Math.max(0, Math.floor(seekTarget / THUMB_INTERVAL) * THUMB_INTERVAL);
    const url = thumbBaseUrl + (thumbBaseUrl.indexOf('?') !== -1 ? '&' : '?') + 't=' + bucket;
    clearTimeout(thumbDebounce);
    thumbDebounce = setTimeout(() => { if (scrubThumb.src !== url) scrubThumb.src = url; }, 140);
  }

  function commitSeek() {
    if (!seeking) return;
    const target = seekTarget, resume = wasPlayingBeforeSeek;
    exitSeekUI();
    doSeek(target, resume);          // resume the pre-scrub play state at the new position
    showBadge('⏩ ' + formatTime(target));
  }

  function cancelSeek() {
    if (!seeking) return;
    const resume = wasPlayingBeforeSeek;
    exitSeekUI();
    if (resume) video.play().catch(() => {}); else video.pause(); // no seek — just restore state
  }

  function exitSeekUI() {
    seeking = false;
    scrub.classList.add('hidden');
    clearTimeout(thumbDebounce);
    scrubThumb.removeAttribute('src');
    scrubThumb.style.opacity = '0';
  }

  // Briefly surface a transport action on-screen — the only feedback a viewer gets
  // that a remote press was received (and a visible confirmation while debugging).
  let badgeTimer = null;
  function showBadge(text) {
    stateBadge.textContent = text;
    stateBadge.classList.add('visible');
    clearTimeout(badgeTimer);
    // Leave the pause badge up; auto-hide transient ones.
    if (text.indexOf('Pausado') === -1) badgeTimer = setTimeout(() => stateBadge.classList.remove('visible'), 1200);
  }

  // ── TV remote via DOM key events ────────────────────────────────────────────
  // Confirmed on this TV (2026-07-04 castlog): the remote is a D-pad — it emits Arrow
  // keys, Enter (select), and Escape (back) as DOM key events; it has NO dedicated media
  // transport keys. So playback is driven entirely off the D-pad:
  //   Enter/Select → play/pause · Left/Right → seek ∓10s · Up/Down → volume ±10%.
  // The MediaPlay/Pause/FF/RW keyCodes stay mapped for other remotes that do send them.
  // Matched by e.key first, then numeric keyCode (TV browsers often report only the latter).
  // The action taken is logged next to the key so the castlog proves the effect, not just
  // that the press arrived.
  function handleRemoteKey(e) {
    const k = e.key, c = e.keyCode;
    let action = null;
    if (k === 'ArrowRight' || k === 'MediaFastForward' || k === 'MediaTrackNext' || c === 39 || c === 417 || c === 228) {
      adjustSeek(SEEK_STEP);  action = 'scrub→' + formatTime(seekTarget);
    } else if (k === 'ArrowLeft' || k === 'MediaRewind' || k === 'MediaTrackPrevious' || c === 37 || c === 412 || c === 227) {
      adjustSeek(-SEEK_STEP); action = 'scrub→' + formatTime(seekTarget);
    } else if (k === ' ' || k === 'Enter' || k === 'MediaPlayPause' || c === 13 || c === 32 || c === 179 || c === 85) {
      // OK commits an in-progress scrub; otherwise it's play/pause.
      if (seeking) { commitSeek(); action = 'commit-seek'; }
      else { togglePlay(); action = video.paused ? 'pause' : 'play'; }
    } else if (k === 'Escape' || k === 'BrowserBack' || k === 'GoBack' || c === 27 || c === 461 || c === 10009) {
      // Back cancels a scrub (and is consumed so it doesn't also exit the app); with no
      // scrub active it's left unmapped so the platform's Back closes the receiver.
      if (seeking) { cancelSeek(); action = 'cancel-seek'; }
    } else if (k === 'MediaPlay' || c === 415) {
      if (seeking) { commitSeek(); action = 'commit-seek'; }
      else { video.play().catch(() => {}); showBadge('▶'); action = 'play'; }
    } else if (k === 'MediaPause' || c === 19) {
      video.pause(); showBadge('⏸ Pausado'); action = 'pause';
    } else if (k === 'MediaStop' || c === 413) {
      if (seeking) { cancelSeek(); action = 'cancel-seek'; }
      else { video.pause(); showBadge('⏸ Pausado'); action = 'stop'; }
    } else if (k === 'ArrowUp' || c === 38) {
      changeVolume(0.1);  action = 'vol+';
    } else if (k === 'ArrowDown' || c === 40) {
      changeVolume(-0.1); action = 'vol-';
    }
    dlog('remote keydown: key="' + e.key + '" code="' + e.code + '" keyCode=' + c +
         (action ? ' → ' + action : ' (unmapped)'));
    if (action) { try { e.preventDefault(); } catch (err) {} }
  }
  // Capture phase (3rd arg true): window-capture runs top-down BEFORE any document/body
  // handler the CAF SDK installs, so we log/handle the key even if CAF's handler then
  // throws (the opaque "Script error." bursts) and stops propagation before the bubble phase.
  window.addEventListener('keydown', handleRemoteKey, true);
  // Log keyup too — some TV remotes surface media keys only on release. Log-only, so it
  // can't double-trigger an action; whichever of keydown/keyup carries the keys shows up
  // in the castlog for #130 discovery.
  window.addEventListener('keyup', (e) =>
    dlog('remote keyup: key="' + e.key + '" code="' + e.code + '" keyCode=' + e.keyCode + ' which=' + e.which), true);

  // ── TV remote via CAF media session ─────────────────────────────────────────
  // The other (and more common) way a physical remote's transport keys reach a receiver
  // is as CAF media messages routed through the PlayerManager's media session. This
  // receiver historically never touched PlayerManager because its LOAD/playback pipeline
  // is broken on this TV — but the message/session layer is separate, so we register
  // command interceptors that drive OUR hls.js <video> and advertise a session, WITHOUT
  // ever calling CAF's load pipeline. Everything is wrapped defensively: any CAF quirk
  // degrades to the DOM-key + custom-channel paths rather than breaking playback.
  function setupRemoteBridge() {
    try {
      playerManager = context.getPlayerManager();
      const messages = cast.framework.messages;
      const MT  = messages.MessageType;
      const CMD = messages.Command;
      const PS  = messages.PlayerState;

      // Tell remotes / the Home app which transport controls this session offers.
      playerManager.setSupportedMediaCommands(
        CMD.PAUSE | CMD.SEEK | CMD.STREAM_VOLUME | CMD.STREAM_MUTE, true
      );

      // Known transport commands → drive our own hls.js <video>.
      const handlers = {
        [MT.PLAY]:  () => { video.play().catch(() => {}); showBadge('▶'); },
        [MT.PAUSE]: () => { video.pause(); showBadge('⏸ Pausado'); },
        [MT.STOP]:  () => { video.pause(); showBadge('⏸ Pausado'); },
        [MT.SEEK]:  (req) => {
          // Absolute target for a scrub; relativeTime for the +/- skip keys on some remotes.
          if (typeof req.currentTime === 'number') { doSeek(req.currentTime); showBadge('⏩'); }
          else if (typeof req.relativeTime === 'number') { seekBy(req.relativeTime); }
        },
        [MT.SET_VOLUME]: (req) => {
          const v = req.volume || {};
          if (typeof v.level === 'number') video.volume = Math.max(0, Math.min(1, v.level));
          if (typeof v.muted === 'boolean') video.muted = v.muted;
          showBadge('🔊');
        },
      };

      // Discovery logging for #130: register a logging interceptor for EVERY media message
      // type, so /api/castlog reveals exactly what the TV remote emits — not just the keys
      // we already handle. Known transport commands are handled + logged; everything else is
      // logged and passed through untouched. The ~1 Hz status polls are excluded so they
      // don't flood the 300-entry ring buffer.
      const NOISY = {};
      NOISY[MT.MEDIA_STATUS] = 1;
      NOISY[MT.GET_STATUS]   = 1;

      let logged = 0;
      const unsupported = [];
      Object.keys(MT).forEach((k) => {
        const type = MT[k];
        if (type === MT.MEDIA_STATUS) return; // owned by the status interceptor below
        try {
          playerManager.setMessageInterceptor(type, (req) => {
            if (!NOISY[type]) dlog('CAF msg: ' + type + summarizeReq(req));
            const h = handlers[type];
            if (h) {
              try { h(req || {}); } catch (e) { dlog('CAF handler err: ' + e); }
              pushCafStatus();
              return null; // handled on our own player — skip CAF's (broken) default pipeline
            }
            return req; // unhandled: logged, passed through so nothing else changes
          });
          logged++;
        } catch (e) { unsupported.push(k); } // enum has types this runtime won't intercept
      });
      if (unsupported.length) dlog('interceptors skipped (unsupported by runtime): ' + unsupported.join(', '));

      // CAF isn't driving our element, so make the status it broadcasts reflect OUR real
      // playback — otherwise the Home app / remote overlay show 0:00 and a wrong state.
      playerManager.setMessageInterceptor(MT.MEDIA_STATUS, (status) => {
        try {
          if (status) {
            status.currentTime = absTime();
            status.playerState = video.paused ? PS.PAUSED : PS.PLAYING;
          }
        } catch (e) {}
        return status;
      });

      dlog('CAF remote bridge ready (logging ' + logged + ' message types)');
    } catch (e) {
      playerManager = null;
      dlog('CAF remote bridge unavailable: ' + e);
    }
  }

  // Compact one-line summary of a media request's interesting fields for the castlog.
  function summarizeReq(req) {
    if (!req) return '';
    const bits = [];
    try {
      if (typeof req.currentTime  === 'number') bits.push('t=' + req.currentTime);
      if (typeof req.relativeTime === 'number') bits.push('rel=' + req.relativeTime);
      if (typeof req.playbackRate === 'number') bits.push('rate=' + req.playbackRate);
      if (req.volume) bits.push('vol=' + JSON.stringify(req.volume));
      if (typeof req.userAction !== 'undefined')     bits.push('action=' + req.userAction);
      if (typeof req.customData !== 'undefined')      bits.push('custom=' + JSON.stringify(req.customData));
    } catch (e) {}
    return bits.length ? ' {' + bits.join(', ') + '}' : '';
  }

  // Publish media info on load so a session exists for the remote to target. Metadata
  // only — this never invokes CAF's load pipeline (the part that's broken on this TV).
  function publishCafSession(title, dur) {
    if (!playerManager) return;
    try {
      const info = new cast.framework.messages.MediaInformation();
      info.contentId    = currentUrl || 'flamestream';
      info.contentType  = 'application/x-mpegurl';
      info.streamType   = cast.framework.messages.StreamType.BUFFERED;
      info.duration     = dur || 0;
      const meta = new cast.framework.messages.GenericMediaMetadata();
      meta.title = title || 'CozyFlame Stream';
      info.metadata = meta;
      playerManager.setMediaInformation(info, false);
      dlog('CAF media session published');
    } catch (e) { dlog('publishCafSession failed: ' + e); }
  }

  function pushCafStatus() {
    if (!playerManager) return;
    try { playerManager.broadcastStatus(true); } catch (e) {}
  }

  // ── Message handling ────────────────────────────────────────────────────────
  function sendStatus() {
    try {
      context.sendCustomMessage(NS, undefined, {
        type: 'status',
        t: video.currentTime + startOffset,
        dur: totalDuration,
        paused: video.paused,
        trackId: activeTrackId,
      });
    } catch (e) {}
    // Keep the CAF media-session progress/state fresh too (drives the remote overlay
    // and the Home app), via the MEDIA_STATUS interceptor that injects our real time.
    pushCafStatus();
  }

  function handleMessage(m) {
    switch (m.type) {
      case 'load':
        exitSeekUI(); // drop any scrub carried over from a previous title
        currentUrl = stripQuery(m.url);
        totalDuration = m.duration || 0;
        tracks = m.tracks || [];
        activeTrackId = m.activeTrackId != null ? m.activeTrackId : null;
        thumbBaseUrl = m.thumbUrl || null; // /api/thumb/<path> for scrub previews
        titleBar.textContent = m.title || '';
        splash.classList.add('hidden');
        titleBar.classList.add('visible');
        setTimeout(() => titleBar.classList.remove('visible'), 5000);
        loadAt(m.startTime || 0);
        publishCafSession(m.title, totalDuration);
        clearInterval(statusTimer);
        statusTimer = setInterval(sendStatus, 1000);
        break;

      case 'play':
        video.play().catch(() => {});
        break;

      case 'pause':
        video.pause();
        break;

      case 'seek':
        doSeek(m.time);
        break;

      case 'setVolume':
        video.volume = Math.max(0, Math.min(1, m.level));
        break;

      case 'setMuted':
        video.muted = !!m.muted;
        break;

      case 'setTrack':
        activeTrackId = m.id != null ? m.id : null;
        if (activeTrackId == null) {
          if (trackEl) trackEl.track.mode = 'hidden';
        } else {
          attachSubtitleTrack(activeTrackId);
        }
        sendStatus();
        break;

      default:
        dlog('unknown message type: ' + m.type);
    }
  }

  context.addCustomMessageListener(NS, (event) => {
    try { handleMessage(event.data || {}); }
    catch (e) { dlog('message handling failed: ' + e); }
  });

  // ── Video element UX ────────────────────────────────────────────────────────
  video.addEventListener('pause',   () => { if (!seeking) { stateBadge.textContent = '⏸ Pausado'; stateBadge.classList.add('visible'); } sendStatus(); });
  video.addEventListener('playing', () => { stateBadge.classList.remove('visible'); sendStatus(); });
  video.addEventListener('waiting', () => { stateBadge.textContent = 'Carregando…'; stateBadge.classList.add('visible'); });
  video.addEventListener('canplay', () => { if (!video.paused) stateBadge.classList.remove('visible'); });
  video.addEventListener('ended',   () => { splash.classList.remove('hidden'); sendStatus(); });

  // ── Start ───────────────────────────────────────────────────────────────────
  setupRemoteBridge();
  context.addEventListener(cast.framework.system.EventType.READY, () => {
    dlog('=== receiver build 2026-07-04i ready (D-pad scrub-to-confirm w/ thumbnail preview) ===');
  });
  // No PlayerManager media session ever exists in this receiver, so the platform's
  // media-based idle timeout would kill the session mid-movie — disable it. The
  // sender ending the session (or TV input change) still closes the app.
  context.start({ disableIdleTimeout: true });
})();
