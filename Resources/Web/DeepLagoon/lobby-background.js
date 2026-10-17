(() => {
  'use strict';
  const source = new URLSearchParams(location.search).get('src');
  const status = (name) => {
    const ack = new Image();
    ack.src = 'res://deeplagoon/Web/DeepLagoon/lobby-background-status?' + name;
  };
  let media;
  const stop = () => {
    if (!media) return;
    if (media instanceof HTMLVideoElement) media.pause();
    media.removeAttribute('src');
    if (media instanceof HTMLVideoElement) media.load();
    media.remove();
    media = null;
  };
  const fail = () => { stop(); status('error'); };
  window.addEventListener('pagehide', stop, { once: true });
  // Also release decoders if CEF reports a hidden document before native closure.
  document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
  if (!source || !source.startsWith('res://deeplagoon/')) { fail(); return; }
  if (/\.webm$/i.test(source)) {
    media = document.createElement('video');
    media.muted = true;
    media.defaultMuted = true;
    media.autoplay = true;
    media.loop = true;
    media.playsInline = true;
    media.disablePictureInPicture = true;
    media.addEventListener('playing', () => status('ready'), { once: true });
  } else if (/\.gif$/i.test(source)) {
    media = document.createElement('img');
    media.addEventListener('load', () => status('ready'), { once: true });
  } else { fail(); return; }
  media.addEventListener('error', fail, { once: true });
  document.body.appendChild(media);
  media.src = source;
  if (media instanceof HTMLVideoElement) media.play().catch(fail);
})();
