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
    const image = new Image();
    image.onload = resolve;
    image.onerror = () => {
      if (attempt < 3) setTimeout(() => transmit(type, payload, attempt + 1).then(resolve, reject), 50);
      else reject(new Error('DeepLagoon bridge request failed: ' + type));
    };
    image.src = root + 'bridge?' + encodeURIComponent(type) + '&' + encodeURIComponent(payload);
  });
  const send = message => {
    const payload = JSON.stringify({ ...message.payload, _sequence: sequence++, _page: page });
    if (payload.length > (message.type === 'panel/state_set' ? 32768 : 8192)) {
      console.error('DeepLagoon bridge payload exceeds limit:', message.type);
      if (message.type !== 'log') sendLog('warn', 'bridge', 'dropped ' + message.type + ': too long');
      return;
    }
    // Serialize requests so a ready or action cannot overtake earlier messages.
    // Robust's res scheme is not FetchEnabled. An image request uses the existing
    // resource callback without navigating or requiring engine changes.
    sending = sending.catch(error => console.error(error)).then(async () => {
      if (payload.length <= 6000) {
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
      // Refused after the retries (the host's queue stayed full): leave a trace in the client log.
      if (message.type !== 'log') sendLog('warn', 'bridge', 'dropped ' + message.type + ': refused');
    });
  };
  // Page warnings and errors for the client log (GameWebView, sawmill tgui.page): cut to 2 KiB and
  // at most 5 a second, so a broken render loop cannot flood the bridge.
  let logWindow = 0;
  let logCount = 0;
  const sendLog = (level, ns, message) => {
    const now = Date.now();
    if (now - logWindow >= 1000) {
      logWindow = now;
      logCount = 0;
    }
    if (++logCount > 5) return;
    send({ type: 'log', payload: {
      level, ns: String(ns || '').slice(0, 64), message: String(message || '').slice(0, 2048) } });
  };
  // Error text outside React (handlers, timers, promises); the bundle strips console output.
  // Recovery itself is the health/error message below.
  window.addEventListener('error', event =>
    sendLog('error', 'window', event.error?.stack || event.message));
  window.addEventListener('unhandledrejection', event =>
    sendLog('error', 'window', 'unhandled rejection: ' + (event.reason?.stack || event.reason)));
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
      if (value.type === 'ready' || value.type === 'close' || value.type === 'suspend' ||
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
    if (event.error) {
      window.__deeplagoonPageFailed = true;
      send({ type: 'health/error', payload: { kind: 'script' } });
    }
  });
  window.addEventListener('unhandledrejection', () => {
    window.__deeplagoonPageFailed = true;
    send({ type: 'health/error', payload: { kind: 'promise' } });
  });
  // All local pages return gameplay keys after non-editable clicks.
  {
    let inputFocused = false;
    let inputTextFocused = false;
    const syncInputFocus = () => {
      const node = document.activeElement;
      const text = !!node && (node.isContentEditable ||
        (node.matches?.('input, textarea, select') && !node.disabled && !node.readOnly));
      const active = text || !!window.__deeplagoonCaptureKeys;
      if (active === inputFocused && text === inputTextFocused) return;
      inputFocused = active;
      inputTextFocused = text;
      send({ type: 'chat-input-focus', payload: { active, text } });
    };
    // Read activeElement after the focus transition, including input-to-input clicks.
    document.addEventListener('focusin', () => queueMicrotask(syncInputFocus));
    document.addEventListener('focusout', () => queueMicrotask(syncInputFocus));
    // CEF can lose native focus while activeElement still points at the input.
    // A later click on that same field must reacquire keyboard focus.
    document.addEventListener('mouseup', () => {
      syncInputFocus();
      // Stock Robust acquires native focus on every mouse click, even when DOM
      // focus did not change. Return it to gameplay after a non-editable click.
      if (!inputFocused) send({ type: 'chat-input-focus', payload: { active: false } });
    });
    window.addEventListener('blur', () => {
      if (!inputFocused) return;
      inputFocused = false;
      send({ type: 'chat-input-focus', payload: { active: false } });
    });
    window.DeepLagoonHealth = {
      syncFocus: () => {
        inputFocused = false;
        syncInputFocus();
        if (!inputFocused) send({ type: 'chat-input-focus', payload: { active: false } });
      },
      ping: () => {
        if (window.__tguiAppBooted__ && !window.__deeplagoonPageFailed) send({ type: 'health/pong', payload: {} });
      },
    };
  }
  // Remote links, popups and wiki requests are not supported in local TGUI windows.
  window.open = () => null;
  let dragging = false;
  let suppressClick = false;
  let previousSelection = '';
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
  document.addEventListener('keydown', () => { if (!dragging) suppressClick = false; }, true);
  document.addEventListener('click', event => {
    if (suppressClick) {
      event.preventDefault();
      event.stopImmediatePropagation();
      suppressClick = false;
      return;
    }
    const link = event.target.closest?.('a');
    if (link && link.href && !link.href.startsWith(root)) event.preventDefault();
  }, true);
  window.addEventListener('load', () => Byond.topic({ type: 'ready' }), { once: true });
})();
