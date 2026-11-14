/* DeepLagoon adapter for TGUI's BYOND API. No shell or arbitrary console access. */
(() => {
  const root = 'res://deeplagoon/Web/DeepLagoon/';
  window.__windowId__ = 'deeplagoon';
  window.__updateQueue__ = [];
  window.update = message => window.__updateQueue__.push(message);
  let sequence = 0;
  const page = Date.now().toString(36) + Math.random().toString(36).slice(2);
  let sending = Promise.resolve();
  const transmit = (type, payload, attempt = 0) => new Promise((resolve, reject) => {
    const posted = window.__deeplagoonPostBridge;
    const url = root + 'bridge' + (posted ? '' : '?' + encodeURIComponent(type) + '&' + encodeURIComponent(payload));
    if (window.__deeplagoonFetchBridge && typeof window.fetch === 'function') {
      window.fetch(url, { mode: 'same-origin', credentials: 'omit', cache: 'no-store',
        ...(posted ? { method: 'POST', headers: { 'Content-Type': 'text/plain;charset=UTF-8' }, body: type + '\n' + payload } : {}) }).then(async response => {
        if (!response.ok) throw new Error('Bridge response: ' + response.status);
        await response.arrayBuffer();
        resolve();
      }).catch(() => {
        if (attempt < 3) {
          setTimeout(() => transmit(type, payload, attempt + 1).then(resolve, reject), 50);
        } else {
          // A mismatched native module must not leave the UI without its bridge.
          window.__deeplagoonFetchBridge = false;
          window.__deeplagoonPostBridge = false;
          legacyTransmit(type, payload).then(resolve, reject);
        }
      });
      return;
    }
    const image = new Image();
    image.onload = resolve;
    image.onerror = () => {
      if (attempt < 3) setTimeout(() => transmit(type, payload, attempt + 1).then(resolve, reject), 50);
      else reject(new Error('DeepLagoon bridge request failed: ' + type));
    };
    image.src = url;
  });
  const legacyTransmit = async (type, payload) => {
    if (payload.length <= 6000) { await transmit(type, payload); return; }
    const count = Math.ceil(payload.length / 2000);
    for (let index = 0; index < count; index++) {
      await transmit('bridge/chunk', JSON.stringify({ messageType: type, index, count,
        text: payload.slice(index * 2000, (index + 1) * 2000) }));
    }
  };
  const encodePayload = payload => JSON.stringify({ ...payload, _sequence: sequence++, _page: page });
  const send = message => {
    const payload = encodePayload(message.payload);
    if (payload.length > (message.type === 'panel/state_set' ? 32768 : 8192)) {
      console.error('DeepLagoon bridge payload exceeds limit:', message.type);
      if (message.type !== 'log') sendLog('warn', 'bridge', 'dropped ' + message.type + ': too long');
      return;
    }
    // Serialize requests so a ready or action cannot overtake earlier messages.
    // The native module supports fetch without creating/decoding an image for each
    // message. Keep the image transport for official modules and dev documents.
    sending = sending.catch(error => console.error(error)).then(async () => {
      const nativeRequestFits = !window.__deeplagoonPostBridge && window.__deeplagoonFetchBridge && typeof window.fetch === 'function' &&
        encodeURIComponent(message.type).length + 1 + encodeURIComponent(payload).length <= 65535;
      if (window.__deeplagoonPostBridge || payload.length <= 6000 || nativeRequestFits) {
        await transmit(message.type, payload);
        return;
      }
      const count = Math.ceil(payload.length / 2000);
      for (let index = 0; index < count; index++) {
        await transmit('bridge/chunk', JSON.stringify({ messageType: message.type, index, count,
          text: payload.slice(index * 2000, (index + 1) * 2000) }));
      }
    }).catch(error => {
      console.error(error);
      if (message.type !== 'log') sendLog('warn', 'bridge', 'dropped ' + message.type + ': refused');
    });
  };
  let logWindow = 0;
  let logCount = 0;
  const sendLog = (level, ns, message) => {
    const now = Date.now();
    if (now - logWindow >= 1000) { logWindow = now; logCount = 0; }
    if (++logCount > 5) return;
    send({ type: 'log', payload: {
      level, ns: String(ns || '').slice(0, 64), message: String(message || '').slice(0, 2048),
    } });
  };
  window.Byond = {
    parseJson: value => typeof value === 'string' ? JSON.parse(value) : value,
    topic: value => {
      if (value.type === 'log') {
        if (value.level === 'warn' || value.level === 'error') sendLog(value.level, value.ns, value.message);
        return;
      }
      if (value.type === 'panel/state_set' && typeof value.panel_state === 'string') {
        try { send({ type: value.type, payload: JSON.parse(value.panel_state) }); } catch {}
        return;
      }
      let payload = value.payload;
      if (typeof payload === 'string') {
        try { payload = JSON.parse(payload); } catch { return; }
      }
      if (value.type === 'ping') {
        window.update(JSON.stringify({ type: 'pingReply', payload }));
        return;
      }
      // Only the explicitly supported TGUI messages enter the C# bridge.
      if (value.type === 'ready' || value.type === 'close' || value.type === 'suspend' || value.type === 'health/presented' ||
          value.type?.startsWith('act/')) {
        send({ type: value.type === 'suspend' ? 'close' : value.type, payload: payload || {} });
      }
    },
    winset: () => {}, // Window geometry belongs to Robust's native window.
    winget: (_id, key) => Promise.resolve(key === 'size' ? `${innerWidth},${innerHeight}` : '0,0'),
    command: () => {},
    loadCss: () => {},
    loadJs: () => {},
  };
  window.DeepLagoonNativeBackground = {
    publish: payload => send({ type: 'native-background', payload }),
  };
  window.addEventListener('error', event => {
    sendLog('error', 'window', event.error?.stack || event.message);
    if (event.error) {
      window.__deeplagoonPageFailed = true;
      send({ type: 'health/error', payload: { kind: 'script' } });
    }
  });
  window.addEventListener('unhandledrejection', event => {
    sendLog('error', 'window', event.reason?.stack || event.reason);
    window.__deeplagoonPageFailed = true;
    send({ type: 'health/error', payload: { kind: 'promise' } });
  });
  // All local pages return gameplay keys after non-editable clicks.
  {
    let inputFocused = false;
    let inputTextFocused = false;
    let pointerPressed = false;
    let pointerReleaseTimer;
    const syncInputFocus = (releaseInactive = false) => {
      const node = document.activeElement;
      const text = !!node && (node.isContentEditable ||
        (node.matches?.('input, textarea, select') && !node.disabled && !node.readOnly));
      const active = text || !!window.__deeplagoonCaptureKeys;
      if (pointerPressed && !active) return;
      if (active === inputFocused && text === inputTextFocused && !(releaseInactive && !active)) return;
      inputFocused = active;
      inputTextFocused = text;
      send({ type: 'chat-input-focus', payload: { active, text } });
    };
    document.addEventListener('focusin', () => queueMicrotask(syncInputFocus));
    document.addEventListener('focusout', () => queueMicrotask(syncInputFocus));
    // Stock WebView needs native keyboard focus to receive mouse-up. Moving DOM
    // focus off an input on pointer-down must not interrupt the click.
    document.addEventListener('pointerdown', () => {
      clearTimeout(pointerReleaseTimer);
      pointerPressed = true;
    }, true);
    const finishPointer = () => {
      clearTimeout(pointerReleaseTimer);
      pointerReleaseTimer = setTimeout(() => {
        pointerPressed = false;
        // Return the focus Robust grabbed for the click, even if DOM focus did not change.
        syncInputFocus(true);
      });
    };
    document.addEventListener('pointerup', finishPointer, true);
    document.addEventListener('pointercancel', finishPointer, true);
    window.addEventListener('blur', () => {
      clearTimeout(pointerReleaseTimer);
      pointerPressed = false;
      if (!inputFocused) return;
      inputFocused = false;
      inputTextFocused = false;
      send({ type: 'chat-input-focus', payload: { active: false } });
    });
    window.DeepLagoonHealth = {
      syncFocus: () => {
        if (pointerPressed) return;
        inputFocused = false;
        syncInputFocus(true);
      },
      ping: () => {
        // Bypass the action queue, but use a fresh URL so the image bridge cannot reuse a cached reply.
        if (window.__tguiAppBooted__ && !window.__deeplagoonPageFailed) transmit('health/pong', encodePayload({})).catch(() => {});
      },
    };
  }
  // Remote links, popups and wiki requests are not supported in local TGUI windows.
  window.open = () => null;
  let dragging = false;
  let suppressClick = false;
  let previousSelection = '';
  let previousTextClick = null;
  const wordSegmenter = typeof Intl.Segmenter === 'function'
    ? new Intl.Segmenter(undefined, { granularity: 'word' }) : null;
  const selectWordOnRepeatedClick = event => {
    // The stock WebView sends two single clicks to CEF. Leave native double-clicks and drag selections alone.
    const previous = previousTextClick;
    previousTextClick = null;
    const field = event.target;
    if (!wordSegmenter || !event.isTrusted || event.detail !== 1 || event.button !== 0 ||
        (event.pointerType && event.pointerType !== 'mouse') ||
        event.shiftKey || event.ctrlKey || event.altKey || event.metaKey ||
        field !== document.activeElement || !field.matches?.('textarea, input:not([type=password])')) return;

    const caret = field.selectionStart;
    if (caret === null || caret !== field.selectionEnd) return;
    if (previous?.target !== field || event.timeStamp - previous.timeStamp > 500 ||
        Math.hypot(event.clientX - previous.clientX, event.clientY - previous.clientY) > 5) {
      previousTextClick = event;
      return;
    }

    const segments = wordSegmenter.segment(field.value);
    const atCaret = segments.containing(caret);
    const word = atCaret?.isWordLike ? atCaret : segments.containing(caret - 1);
    if (word?.isWordLike) field.setSelectionRange(word.index, word.index + word.segment.length);
  };
  window.addEventListener('deeplagoon/window-drag', event => {
    dragging = event.detail === true;
    if (dragging) {
      suppressClick = true;
      previousSelection = document.documentElement.style.userSelect;
      document.documentElement.style.userSelect = 'none';
      window.getSelection()?.removeAllRanges();
    } else {
      document.documentElement.style.userSelect = previousSelection;
    }
  });
  document.addEventListener('mousedown', () => { if (!dragging) suppressClick = false; }, true);
  document.addEventListener('keydown', () => {
    previousTextClick = null;
    if (!dragging) suppressClick = false;
  }, true);
  window.addEventListener('blur', () => { previousTextClick = null; });
  document.addEventListener('click', event => {
    if (suppressClick) {
      previousTextClick = null;
      event.preventDefault();
      event.stopImmediatePropagation();
      suppressClick = false;
      return;
    }
    selectWordOnRepeatedClick(event);
    const link = event.target.closest?.('a');
    if (link && link.href && !link.href.startsWith(root)) event.preventDefault();
  }, true);
  window.addEventListener('load', () => Byond.topic({ type: 'ready' }), { once: true });
})();
