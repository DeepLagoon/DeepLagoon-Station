import { useEffect, useRef, useState } from 'react';
import { readArtDraft, writeArtDraft, deleteArtDraft, canRecoverArtDraft } from './artDraftRecovery.mjs';

const writerId = () => [...crypto.getRandomValues(new Uint8Array(16))].map(n => n.toString(16).padStart(2, '0')).join('');
export function useArtDraft(options) {
  const current = useRef(options); current.current = options;
  const [status, setStatus] = useState('');
  const state = useRef(null);
  if (!state.current) state.current = { key: options.data.recoveryKey, epoch: options.data.draftEpoch || 0,
    version: options.data.draftVersion || 0, writer: writerId(), sequence: 0, serial: 0, destroyed: false, confirmed: 0 };
  const c = state.current;
  const label = key => current.current.data.labels?.[key] || key;
  const show = key => { if (!c.destroyed) setStatus(key ? label(key) : ''); };
  function cancel() {
    c.serial++; clearTimeout(c.timer); clearTimeout(c.progress); clearTimeout(c.timeout);
    c.timer = c.progress = c.timeout = null; c.flight = null;
  }
  function reset(data = current.current.data, remove = true) {
    cancel(); if (remove && c.key) deleteArtDraft(c.key, c).catch(() => {});
    c.key = data.recoveryKey; c.epoch = data.draftEpoch || 0; c.version = data.draftVersion || 0;
    c.writer = writerId(); c.sequence = 0; c.confirmed = 0; c.pending = null; c.last = null; c.backup = null; show('');
  }
  function retry() {
    clearTimeout(c.timer);
    if (!c.destroyed && c.pending) c.timer = setTimeout(send, 1500);
  }
  function acknowledge(ack) {
    const flight = c.flight;
    if (!flight || ack.writer !== c.writer || ack.sequence !== flight.entry.sequence || ack.epoch !== c.epoch) return;
    clearTimeout(c.timeout); c.timeout = null; c.flight = null;
    if (ack.success) { c.version = ack.version; c.confirmed = Math.max(c.confirmed, ack.sequence); }
    if (ack.success) show(c.pending ? 'draft-saving' : 'draft-saved');
    else { c.pending ||= flight.entry; show('draft-failed'); }
    retry();
  }
  async function send() {
    c.timer = null;
    if (c.destroyed || !c.key || !c.pending || c.flight) return;
    if (current.current.blocked()) { retry(); return; }
    const entry = c.pending; c.pending = null;
    const flight = { entry, serial: c.serial }; c.flight = flight;
    const chunk = current.current.data.chunkSize || 5000, count = Math.ceil(entry.document.length / chunk);
    try {
      for (let index = 0; index < count; index++) {
        if (c.destroyed || c.flight !== flight || flight.serial !== c.serial) return;
        if (current.current.blocked()) { c.flight = null; c.pending ||= entry; retry(); return; }
        await current.current.act('draft', { writer: c.writer, sequence: entry.sequence, epoch: c.epoch, version: c.version,
          text: entry.document.slice(index * chunk, (index + 1) * chunk), index, count });
        if (!current.current.data.external && index + 1 < count) await new Promise(resolve => setTimeout(resolve, 25));
      }
      if (c.flight === flight) c.timeout = setTimeout(() => acknowledge({ writer: c.writer, sequence: entry.sequence, epoch: c.epoch, version: c.version, success: false }), 120000);
    } catch { if (c.flight === flight) acknowledge({ writer: c.writer, sequence: entry.sequence, epoch: c.epoch, version: c.version, success: false }); }
  }
  function capture() {
    if (!c.key || c.destroyed || current.current.localBlocked()) return false;
    clearTimeout(c.progress); c.progress = null;
    const document = current.current.document(); if (!document) return false;
    if (document.length > current.current.data.maxDocument) { show('too-large'); return false; }
    if (c.last?.document === document) return true;
    const entry = { key: c.key, epoch: c.epoch, baseVersion: c.version, writer: c.writer, sequence: ++c.sequence, document, updated: Date.now() };
    c.last = entry; c.pending = entry;
    c.backup = writeArtDraft(entry).then(() => true, () => {
      if (!c.destroyed && c.pending) show('draft-failed');
      return false;
    });
    show('draft-saving');
    if (!c.timer && !c.flight) c.timer = setTimeout(send, 750);
    return true;
  }
  async function flush(localFallback) {
    if (!capture() || current.current.data.draftDisabled) return false;
    const sequence = c.sequence, serial = c.serial, started = Date.now();
    let backedUp = false;
    if (localFallback) c.backup?.then(success => { backedUp = success; });
    clearTimeout(c.timer); c.timer = null; send();
    while (!c.destroyed && c.serial === serial && !current.current.data.draftDisabled) {
      if (c.confirmed >= sequence) return true;
      // The same canvas can recover a committed IndexedDB copy on its next open.
      const elapsed = Date.now() - started;
      if (backedUp && elapsed >= 1500) return true;
      if (elapsed >= 120000) break;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    return false;
  }
  function progress() {
    if (!c.progress && !c.destroyed) c.progress = setTimeout(() => { c.progress = null; capture(); }, 750);
  }
  async function recover(data) {
    if (!c.key) return { document: data.document, local: false };
    try {
      const record = await readArtDraft(c.key);
      if (canRecoverArtDraft(record, data)) {
        c.writer = record.writer; c.sequence = record.sequence; c.version = data.draftVersion || 0;
        show('draft-restored'); return { document: record.document, local: true };
      }
      if (record) deleteArtDraft(c.key, record).catch(() => {});
    } catch { /* The server draft remains available when local browser storage is unavailable. */ }
    return { document: data.document, local: false };
  }
  useEffect(() => {
    c.destroyed = false;
    const ack = e => acknowledge(e.detail);
    const flush = () => { if (current.current.dirty()) capture(); };
    window.addEventListener('art-draft', ack); window.addEventListener('pagehide', flush);
    return () => {
      flush(); c.destroyed = true; cancel();
      window.removeEventListener('art-draft', ack); window.removeEventListener('pagehide', flush);
    };
  }, []);
  useEffect(() => {
    if (c.key !== options.data.recoveryKey || c.epoch !== (options.data.draftEpoch || 0)) {
      reset();
      setTimeout(() => { if (!c.destroyed && current.current.dirty()) capture(); }, 0);
    }
  }, [options.data.recoveryKey, options.data.draftEpoch]);
  return { status, recover, capture, progress, flush, metadata: () => ({ writer: c.writer, sequence: c.sequence, epoch: c.epoch, version: c.version }),
    pause: () => { const pending = c.pending || c.flight?.entry; cancel(); c.pending = pending; retry(); },
    clear: () => reset(), replace: data => reset(data), acknowledge };
}
