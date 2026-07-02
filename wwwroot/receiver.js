// Custom CAF receiver logic for FlameStream.
//
// Problem: our backend serves a *growing* HLS playlist while a title is still being
// transcoded (EXT-X-PLAYLIST-TYPE:EVENT, no EXT-X-ENDLIST yet) and only starts encoding
// a requested position on demand via /stream/*.m3u8?start=<seconds> (see HlsService in
// the backend). The web sender (home.component.ts) already knows to re-request that URL
// with a new ?start= when the user seeks past what's currently loaded. The default
// <cast-media-player>/Shaka Player used here has no idea that scheme exists, so a native
// SEEK past the currently-known manifest range just stalls.
//
// This script intercepts LOAD/SEEK/MEDIA_STATUS so the *receiver* (the one thing that
// actually knows what's currently loaded) can do the same "reload at a new offset" trick
// on its own, without any change needed on the sender side.
(function () {
  const context = cast.framework.CastReceiverContext.getInstance();
  const playerManager = context.getPlayerManager();

  // How close to the edge of the known manifest we still allow a native (instant) seek.
  const SEEK_SAFETY_MARGIN_SECONDS = 2;

  // Absolute-time offset of segment 0 in whatever manifest is currently loaded — i.e.
  // the value of the `start` query param used to load it (0 for the initial/base load).
  let streamStartOffset = 0;

  function parseStartParam(url) {
    if (!url) return 0;
    try {
      const parsed = new URL(url, self.location.href);
      const start = parsed.searchParams.get('start');
      return start ? parseFloat(start) || 0 : 0;
    } catch (e) {
      return 0;
    }
  }

  function stripQuery(url) {
    const idx = url.indexOf('?');
    return idx === -1 ? url : url.substring(0, idx);
  }

  // Single source of truth for streamStartOffset: every load — the sender's original
  // one, or one we trigger ourselves below — flows through here.
  playerManager.setMessageInterceptor(
    cast.framework.messages.MessageType.LOAD,
    (loadRequestData) => {
      const media = loadRequestData && loadRequestData.media;
      const url = (media && (media.contentUrl || media.contentId)) || '';
      streamStartOffset = parseStartParam(url);
      console.log('[FlameStream receiver] LOAD, streamStartOffset =', streamStartOffset);
      return loadRequestData;
    }
  );

  playerManager.setMessageInterceptor(
    cast.framework.messages.MessageType.SEEK,
    (seekRequestData) => {
      const target = seekRequestData && seekRequestData.currentTime;
      if (typeof target !== 'number' || isNaN(target)) return seekRequestData;

      // mediaInformation.duration reflects how much of the (possibly still-growing)
      // manifest Shaka currently knows about. NOTE: unverified against a real device —
      // if this ever reports a non-finite/undefined value for our EVENT-type playlists
      // (e.g. if CAF classifies an in-progress transcode as a LIVE stream rather than
      // BUFFERED), we deliberately fall through to "always reload", which is always
      // correct, just occasionally does an unnecessary reload for an already-available
      // position. Re-tune this once tested against real Chromecast hardware.
      const status = playerManager.getMediaStatus();
      const knownDuration = status && status.mediaInformation && status.mediaInformation.duration;

      if (typeof knownDuration === 'number' && isFinite(knownDuration) && knownDuration > 0) {
        const availableStart = streamStartOffset;
        const availableEnd = streamStartOffset + knownDuration - SEEK_SAFETY_MARGIN_SECONDS;

        if (target >= availableStart && target <= availableEnd) {
          // Already within what's been transcoded — let the native seek proceed,
          // just translate the absolute target back to manifest-local time.
          seekRequestData.currentTime = target - streamStartOffset;
          return seekRequestData;
        }
      }

      // Out of range (or unknown range) — mirror the web player's reloadFrom(): cancel
      // this seek and issue a fresh LOAD at the target offset instead. The LOAD
      // interceptor above will pick up the new streamStartOffset once it lands.
      const mediaInfo = playerManager.getMediaInformation();
      if (!mediaInfo) return seekRequestData;

      console.log('[FlameStream receiver] Seek to', target, 'is beyond known range — reloading');

      const baseUrl = stripQuery(mediaInfo.contentUrl || mediaInfo.contentId);
      const newUrl = `${baseUrl}?start=${target}`;

      const newMediaInfo = Object.assign(new cast.framework.messages.MediaInformation(), mediaInfo);
      newMediaInfo.contentId = newUrl;
      newMediaInfo.contentUrl = newUrl;

      const newLoadRequest = new cast.framework.messages.LoadRequestData();
      newLoadRequest.media = newMediaInfo;
      newLoadRequest.autoplay = true;
      if (status && status.activeTrackIds) newLoadRequest.activeTrackIds = status.activeTrackIds;

      playerManager.load(newLoadRequest);
      return null; // cancel the native seek; the reload above replaces it
    }
  );

  // Outgoing status always reports manifest-local time; add the offset back so the
  // sender's scrubber/polling (cast.service.ts) sees correct, monotonic absolute time
  // across a receiver-triggered reload without any sender-side change.
  playerManager.setMessageInterceptor(
    cast.framework.messages.MessageType.MEDIA_STATUS,
    (status) => {
      if (status && typeof status.currentTime === 'number') {
        status.currentTime = status.currentTime + streamStartOffset;
      }
      return status;
    }
  );

  context.start();
})();
