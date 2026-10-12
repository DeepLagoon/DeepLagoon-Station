/**
 * @file
 * @copyright 2020 Aleksej Komarov
 * @license MIT
 */

// Styles. DeepLagoon: the SS13 themes (styles/themes/*) are not imported: no interface
// selects one, the windows use theme-dark/theme-light from playerTheme. Every window parses
// this CSS, and the themes were about 70% of it.
import './styles/main.scss';

import { perf } from 'common/perf';
import { Component as ReactComponent } from 'react';
import { setupHotReloading } from 'tgui-dev-server/link/client.cjs';

import { FindBar } from './components/FindBar';
import { isDragOrResizeActive } from './drag';
import { setupGlobalEvents } from './events';
import { setupHotKeys } from './hotkeys';
import { captureExternalLinks } from './links';
import { createLogger } from './logging';
import { createRenderer } from './renderer';
import { getRoutedComponent } from './routes';
import { configureStore } from './store';

perf.mark('inception', window.performance?.timing?.navigationStart);
perf.mark('init');
window.__tguiBundleLoaded__ = true;
window.__tguiAppBooted__ = false;
window.__pushTguiDebugEvent__?.('bundleLoaded', {
  bundle: 'tgui',
});

const store = configureStore();

const logger = createLogger('render');

/**
 * DeepLagoon: an exception in an interface used to unmount the whole React root and leave an
 * empty window. Show what broke and a way to reload the window; the error goes to the client log.
 */
class ErrorBoundary extends ReactComponent {
  state = { error: null };

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error, info) {
    logger.error('interface render error', error, info?.componentStack);
  }

  render() {
    const { error } = this.state;
    if (!error) {
      return this.props.children;
    }
    return (
      <div style={{ padding: '16px', color: '#ddd', background: '#171c24', height: '100%' }}>
        <p>Интерфейс окна сломался. / The window interface failed.</p>
        <pre style={{ whiteSpace: 'pre-wrap', opacity: 0.7 }}>{String(error?.message ?? error).slice(0, 300)}</pre>
        <button type="button" onClick={() => location.reload()}>
          Перезагрузить окно / Reload window
        </button>
      </div>
    );
  }
}

const getFindBarInstanceKey = () => {
  const backend = store.getState()?.backend;
  const windowKey = backend?.config?.window?.key || window.__windowId__ || 'tgui';
  const sessionKey = backend?.suspended || 'active';
  return `${windowKey}:${sessionKey}`;
};

const renderApp = createRenderer(() => {
  const Component = getRoutedComponent(store);
  return (
    <>
      <ErrorBoundary key={store.getState()?.backend?.config?.interface}>
        <Component />
      </ErrorBoundary>
      <FindBar key={getFindBarInstanceKey()} />
    </>
  );
});

// During drag/resize, defer renders to requestAnimationFrame so pending
// mousemove events are processed first, keeping window movement smooth.
// Content still updates (once per frame via RAF) — only the scheduling changes.
let dragRafId = null;

const renderAppIfIdle = () => {
  if (isDragOrResizeActive()) {
    if (dragRafId === null) {
      dragRafId = requestAnimationFrame(() => {
        dragRafId = null;
        renderApp();
      });
    }
    return;
  }
  if (dragRafId !== null) {
    cancelAnimationFrame(dragRafId);
    dragRafId = null;
  }
  renderApp();
};

const setupApp = () => {
  // Delay setup
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupApp);
    return;
  }

  setupGlobalEvents();
  setupHotKeys();
  captureExternalLinks();

  if (process.env.NODE_ENV !== 'production') {
    setupHotReloading();
  }

  // Subscribe for state updates
  store.subscribe(renderAppIfIdle);

  // Dispatch incoming messages
  const dispatchIncomingMessage = msg => {
    window.__recordIncomingTguiMessage__?.(msg);
    store.dispatch(Byond.parseJson(msg));
  };
  window.update = dispatchIncomingMessage;

  // Process the early update queue
  window.__pushTguiDebugEvent__?.('appSetupBegin', {
    bundle: 'tgui',
    queuedBeforeDrain: window.__updateQueue__?.length || 0,
  });
  while (true) {
    const msg = window.__updateQueue__.shift();
    if (!msg) {
      break;
    }
    store.dispatch(Byond.parseJson(msg));
  }
  window.__tguiAppBooted__ = true;
  window.__pushTguiDebugEvent__?.('appBooted', {
    bundle: 'tgui',
    queuedAfterDrain: window.__updateQueue__?.length || 0,
  });

};

setupApp();
