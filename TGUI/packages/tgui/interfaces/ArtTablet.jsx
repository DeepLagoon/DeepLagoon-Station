import { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ArtStudioEditor } from './ArtStudio';

// The one-use ticket stays in the fragment, never in HTTP URLs or referrers.
export const ArtTablet = () => {
  const [data, setData] = useState(null), [message, setMessage] = useState('Открываем холст…'), [closed, setClosed] = useState(false);
  const token = useRef(''), state = useRef(null), upload = useRef({}), sending = useRef(false), draftSending = useRef(false), mounted = useRef(true), draft = useRef(false);
  state.current = data;
  const storageKey = 'deeplagoon-art:' + location.pathname;
  async function request(action, body, revision, metadata) {
    const headers = { Authorization: 'Bearer ' + token.current };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (revision !== undefined) headers['If-Match'] = String(revision);
    if (metadata) for (const key of ['writer', 'sequence', 'epoch', 'version']) headers['X-Art-Draft-' + key] = String(metadata[key]);
    const response = await fetch(action, { method: action === 'session' || action === 'ping' ? 'GET' : 'POST', headers, body, cache: 'no-store', credentials: 'omit', signal: AbortSignal.timeout(120000) });
    if (!response.ok) throw new Error(response.status === 410 || response.status === 401 ? 'expired' : 'unavailable');
    return response.json();
  }
  const expired = () => {
    sessionStorage.removeItem(storageKey); token.current = '';
    if (state.current) setData(s => ({ ...s, result: -Math.abs(s.result || 0) - 1, status: s.labels['tablet-error'], draftDisabled: true }));
    else { setClosed(true); setMessage('Доступ к холсту завершён. Откройте редактор заново из игры.'); }
  };
  useEffect(() => {
    mounted.current = true;
    (async () => {
      try {
        const ticket = location.hash.slice(1);
        history.replaceState(null, '', location.pathname);
        let result;
        if (ticket) {
          token.current = ticket; result = await request('claim'); token.current = result.token;
          sessionStorage.setItem(storageKey, token.current);
        } else { token.current = sessionStorage.getItem(storageKey) || ''; result = await request('session'); }
        if (mounted.current) setData(result.data);
      } catch { if (mounted.current) expired(); }
    })();
    const interval = setInterval(async () => {
      if (!state.current || sending.current || !token.current) return;
      try { await request('ping'); } catch (e) { if (mounted.current && e.message === 'expired') expired(); }
    }, 10000);
    const beforeUnload = e => { if (Object.keys(upload.current).length || draft.current) { e.preventDefault(); e.returnValue = ''; } };
    window.addEventListener('beforeunload', beforeUnload);
    return () => { mounted.current = false; clearInterval(interval); window.removeEventListener('beforeunload', beforeUnload); };
  }, []);
  const act = async (action, payload = {}) => {
    if (action === '__style-blocked') { draft.current = payload.blocked; return; }
    if (action === 'close') {
      if (sending.current) return;
      try { await request('release'); sessionStorage.removeItem(storageKey); token.current = ''; setClosed(true); setMessage(data.labels['tablet-closed']); }
      catch (e) { if (e.message === 'expired') expired(); else setData(s => ({ ...s, status: s.labels['tablet-error'] })); }
      return;
    }
    if (action === 'discard') {
      try { const result = await request('discard'); if (mounted.current) setData(result.data); return true; }
      catch (e) { if (e.message === 'expired') expired(); else if (mounted.current) setData(s => ({ ...s, status: s.labels['tablet-error'] })); return false; }
    }
    if (action !== 'save' && action !== 'draft' || sending.current) return;
    const ack = detail => window.dispatchEvent(new CustomEvent('art-draft', { detail }));
    if (action === 'draft' && draftSending.current) { ack({ ...payload, success: false }); return; }
    if (payload.index === 0) upload.current[action] = { parts: [], count: payload.count, metadata: payload };
    const current = upload.current[action];
    if (!current || current.parts.length !== payload.index || current.count !== payload.count || payload.text.length > data.chunkSize) return;
    current.parts.push(payload.text);
    if (current.parts.length !== current.count) return;
    delete upload.current[action];
    if (action === 'draft') {
      draftSending.current = true;
      try { const result = await request('draft', current.parts.join(''), state.current.revision, current.metadata); ack(result.ack); }
      catch (e) { ack({ ...current.metadata, success: false }); if (e.message === 'expired' && mounted.current) expired(); }
      finally { draftSending.current = false; }
      return;
    }
    sending.current = true;
    try {
      const live = await request('ping');
      const result = await request('save', current.parts.join(''), live.revision);
      if (mounted.current) setData(result.data);
    } catch (e) {
      if (mounted.current) {
        if (e.message === 'expired') expired();
        else setData(s => ({ ...s, result: -Math.abs(s.result || 0) - 1, status: s.labels['tablet-error'] }));
      }
    } finally { sending.current = false; }
  };
  if (closed || !data) return <div className="ArtStudio"><div className="ArtStudio__dialog" role="status">{message}</div></div>;
  return <ArtStudioEditor data={data} act={act} />;
};

export const mountArtTablet = () => createRoot(document.getElementById('react-root')).render(<ArtTablet />);
