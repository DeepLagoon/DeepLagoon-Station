/**
 * @file
 * @copyright 2020 Aleksej Komarov
 * @license MIT
 */

// Themes
import './styles/main.scss';

import { perf } from 'common/perf';
import { setupHotReloading } from 'tgui-dev-server/link/client.cjs';
import { useLayoutEffect } from 'react';
import { sendMessage } from './backend';

import { FindBar } from './components/FindBar';
import { InterfaceErrorBoundary } from './components/InterfaceErrorBoundary';
import { isDragOrResizeActive } from './drag';
import { setupGlobalEvents } from './events';
import { setupHotKeys } from './hotkeys';
import { captureExternalLinks } from './links';
import { createRenderer } from './renderer';
import { getRoutedComponent } from './routes';
import { configureStore } from './store';
import { mountArtTablet } from './interfaces/ArtTablet';

perf.mark('inception', window.performance?.timing?.navigationStart);
perf.mark('init');
window.__tguiBundleLoaded__ = true;
window.__tguiAppBooted__ = false;
window.__pushTguiDebugEvent__?.('bundleLoaded', {
  bundle: 'tgui',
});

const store = configureStore();

// Confirm a committed interface, then allow a paint before the native host reveals it.
const PresentationReady = ({ presentation }) => {
  useLayoutEffect(() => {
    if (presentation === undefined) return;
    let frame = requestAnimationFrame(() => {
      frame = requestAnimationFrame(() => sendMessage({ type: 'health/presented', payload: { presentation } }));
    });
    return () => cancelAnimationFrame(frame);
  }, [presentation]);
  return null;
};

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
      <InterfaceErrorBoundary key={store.getState()?.backend?.config?.interface}>
        <Component />
      </InterfaceErrorBoundary>
      <PresentationReady presentation={store.getState()?.backend?.config?.presentation} />
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

  if (document.documentElement.dataset.artTablet === 'true') {
    mountArtTablet();
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
    const message = Byond.parseJson(msg);
    if (message.type === 'art/draft') { window.dispatchEvent(new CustomEvent('art-draft', { detail: message.payload })); return; }
    if (message.type === 'console/key') {
      window.dispatchEvent(new CustomEvent('deeplagoon/console-key', { detail: message.payload }));
      return;
    }
    store.dispatch(message);
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
