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

  // ── Debug overlay + backend log relay ───────────────────────────────────────
  // Set DEBUG = false for normal viewing. dlog() lines go to: console, the compact
  // on-TV overlay, and POST /api/castlog (readable from any machine on the LAN).
  const DEBUG = true;
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
    if (!DEBUG) return;
    logLines.push(msg);
    while (logLines.length > 14) logLines.shift();
    renderLog();
    try { fetch('/api/castlog', { method: 'POST', body: msg }); } catch (e) {}
  }
  window.addEventListener('error', (e) => dlog('JS ERROR: ' + (e.message || '?') + ' @' + (e.filename || '?') + ':' + (e.lineno || '?')));
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
  function loadAt(absoluteStart) {
    destroyHls();
    startOffset = absoluteStart; // provisional until X-Hls-Start-Offset corrects it

    const url = absoluteStart > 0 ? currentUrl + '?start=' + absoluteStart : currentUrl;
    dlog('loadAt ' + absoluteStart.toFixed(1) + 's');

    if (currentUrl.indexOf('.m3u8') === -1) {
      // Direct file (progressive MP4) — native playback, native seeking.
      video.src = currentUrl;
      video.currentTime = absoluteStart;
      video.play().catch(() => {});
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
      video.play().catch(() => {});
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
  }

  function handleMessage(m) {
    switch (m.type) {
      case 'load':
        currentUrl = stripQuery(m.url);
        totalDuration = m.duration || 0;
        tracks = m.tracks || [];
        activeTrackId = m.activeTrackId != null ? m.activeTrackId : null;
        titleBar.textContent = m.title || '';
        splash.classList.add('hidden');
        titleBar.classList.add('visible');
        setTimeout(() => titleBar.classList.remove('visible'), 5000);
        loadAt(m.startTime || 0);
        clearInterval(statusTimer);
        statusTimer = setInterval(sendStatus, 1000);
        break;

      case 'play':
        video.play().catch(() => {});
        break;

      case 'pause':
        video.pause();
        break;

      case 'seek': {
        const target = m.time;
        const local = target - startOffset;
        let buffered = false;
        for (let i = 0; i < video.buffered.length; i++) {
          if (local >= video.buffered.start(i) - 1 && local <= video.buffered.end(i)) { buffered = true; break; }
        }
        // Direct files seek natively anywhere; HLS reloads when outside the buffer
        // so the backend can start a seek transcode if needed (mirrors the web app).
        if (buffered || currentUrl.indexOf('.m3u8') === -1) {
          video.currentTime = currentUrl.indexOf('.m3u8') === -1 ? target : local;
        } else {
          loadAt(target);
        }
        sendStatus();
        break;
      }

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
  video.addEventListener('pause',   () => { stateBadge.textContent = '⏸ Pausado'; stateBadge.classList.add('visible'); sendStatus(); });
  video.addEventListener('playing', () => { stateBadge.classList.remove('visible'); sendStatus(); });
  video.addEventListener('waiting', () => { stateBadge.textContent = 'Carregando…'; stateBadge.classList.add('visible'); });
  video.addEventListener('canplay', () => { if (!video.paused) stateBadge.classList.remove('visible'); });
  video.addEventListener('ended',   () => { splash.classList.remove('hidden'); sendStatus(); });

  // ── Start ───────────────────────────────────────────────────────────────────
  context.addEventListener(cast.framework.system.EventType.READY, () => {
    dlog('=== receiver build 2026-07-04b ready (self-driven hls.js playback) ===');
  });
  // No PlayerManager media session ever exists in this receiver, so the platform's
  // media-based idle timeout would kill the session mid-movie — disable it. The
  // sender ending the session (or TV input change) still closes the app.
  context.start({ disableIdleTimeout: true });
})();
