const DATABASE = 'deeplagoon-art-drafts';
const STORE = 'drafts';
const MAX_BYTES = 32 * 1024 * 1024, MAX_ENTRIES = 8, MAX_AGE = 7 * 24 * 60 * 60 * 1000;
let database, queue = Promise.resolve();
const open = () => database ||= new Promise((resolve, reject) => {
  const request = indexedDB.open(DATABASE, 1);
  request.onupgradeneeded = () => request.result.createObjectStore(STORE, { keyPath: 'key' });
  request.onsuccess = () => resolve(request.result);
  request.onerror = () => reject(request.error);
});
const operation = action => {
  const next = queue.catch(() => {}).then(action);
  queue = next.catch(() => {}); return next;
};
export const readArtDraft = key => operation(async () => {
  const db = await open();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, 'readwrite'), store = tx.objectStore(STORE), request = store.get(key);
    let value = null;
    request.onsuccess = () => {
      value = request.result || null;
      if (value && value.updated < Date.now() - MAX_AGE) { store.delete(key); value = null; }
    };
    tx.oncomplete = () => resolve(value); tx.onerror = tx.onabort = () => reject(tx.error);
  });
});
export const writeArtDraft = record => operation(async () => {
  if (record.document.length > 8 * 1024 * 1024) throw new Error('draft-too-large');
  const db = await open();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, 'readwrite'), store = tx.objectStore(STORE);
    const item = { ...record, updated: record.updated || Date.now() };
    const existing = store.get(item.key);
    existing.onsuccess = () => {
      const old = existing.result;
      if (old && (old.epoch > item.epoch || old.epoch === item.epoch &&
        (old.updated > item.updated || old.writer === item.writer && old.sequence > item.sequence))) return;
      store.put(item);
      const request = store.getAll();
      request.onsuccess = () => {
        let entries = request.result.sort((a, b) => a.updated - b.updated);
        for (const old of entries.filter(e => e.updated < Date.now() - MAX_AGE)) store.delete(old.key);
        entries = entries.filter(e => e.updated >= Date.now() - MAX_AGE);
        let bytes = entries.reduce((sum, e) => sum + e.document.length * 2, 0), count = entries.length;
        for (const old of entries) {
          if (count <= MAX_ENTRIES && bytes <= MAX_BYTES) break;
          if (old.key === item.key) continue;
          store.delete(old.key); count--; bytes -= old.document.length * 2;
        }
      };
    };
    tx.oncomplete = () => resolve(); tx.onerror = tx.onabort = () => reject(tx.error);
  });
});
export const deleteArtDraft = (key, expected) => {
  // Snapshot the caller's identity before the queued transaction runs.
  const match = expected && { epoch: expected.epoch, writer: expected.writer, sequence: expected.sequence };
  return operation(async () => {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite'), store = tx.objectStore(STORE);
      if (!match) store.delete(key);
      else {
        const request = store.get(key);
        request.onsuccess = () => {
          const old = request.result;
          if (old && old.epoch === match.epoch && old.writer === match.writer && old.sequence <= match.sequence) store.delete(key);
        };
      }
      tx.oncomplete = () => resolve(); tx.onerror = tx.onabort = () => reject(tx.error);
    });
  });
};

export function canRecoverArtDraft(record, data) {
  return !!record && typeof record.document === 'string' && record.document.length <= 8 * 1024 * 1024 && /^[0-9a-f]{32}$/i.test(record.writer) && Number.isInteger(record.sequence) && record.sequence > 0 && record.epoch === (data.draftEpoch || 0) &&
    (record.baseVersion === (data.draftVersion || 0) ||
      record.writer === data.draftWriter && record.sequence > (data.draftSequence || 0));
}
