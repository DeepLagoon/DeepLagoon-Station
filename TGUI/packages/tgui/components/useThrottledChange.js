import { useCallback, useEffect, useRef } from 'react';

// Drag controls render locally while the host receives at most one change per interval.
export const useThrottledChange = (callback, interval = 50) => {
  const latest = useRef(callback);
  latest.current = callback;
  const state = useRef({ timer: null, pending: undefined, last: -Infinity });
  const flush = useCallback(() => {
    const current = state.current;
    clearTimeout(current.timer);
    current.timer = null;
    if (current.pending === undefined) return;
    const value = current.pending;
    current.pending = undefined;
    current.last = performance.now();
    latest.current(value);
  }, []);
  const send = useCallback(value => {
    const current = state.current;
    current.pending = value;
    const wait = interval - (performance.now() - current.last);
    if (wait <= 0) flush();
    else if (current.timer === null) current.timer = setTimeout(flush, wait);
  }, [flush, interval]);
  // A replaced profile/control must never receive the previous control's delayed edit.
  useEffect(() => () => {
    clearTimeout(state.current.timer);
    state.current.timer = null;
    state.current.pending = undefined;
  }, []);
  return [send, flush];
};
