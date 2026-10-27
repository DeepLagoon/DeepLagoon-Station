import { useInterfaceStyleGuard } from './useInterfaceStyleGuard';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { editableItem, scopeDecalChanges, decalTemplate, collaborativeDraft, discardSuperseded, cellsBetween, trackLocalEdits, rebaseExpansion, resizeExpansion, copyItems, inside, stamp, draftIssues, makeTemplate, projectTemplate, insertTemplate, makePatch, itemsAtPoint, editorKey, indexItems, nearbyItems, ensureWallFloors, cellRectangle, eraseRectangle, moveSelection, drawOrder, replaceConflicts, mirrorChanges, reflectSelection } from './gridEditorModel.mjs';
import './GridEditor.scss';
import { useGridEditorReference } from './GridEditorReference';
import { GridEditorDecalSprite } from './GridEditorDecalSprite';
import { GridEditorScene, GridEditorSelection } from './GridEditorScene';
import { GridEditorSelect } from './GridEditorSelect';
import { GridEditorAtmos } from './GridEditorAtmos';
import { GridEditorIssues } from './GridEditorIssues';
import { GridEditorPeers, useGridEditorPeers } from './GridEditorPeers';
import { useGridEditorRoutes, GridEditorNetworks, GridEditorRouteHandles } from './GridEditorRoutes';
import { routeProfile } from './gridEditorRouting.mjs';
import { exportGridDraft, draftFileChunks } from './gridEditorExport.mjs';

const categories = [['tile', 'Полы'], ['wall', 'Стены'], ['entity', 'Объекты'], ['decal', 'Декали'], ['all', 'Все'], ['favorites', '★ Избранное'], ['templates', 'Шаблоны']];
const tools = [['pan', 'Перемещение · 1'], ['brush', 'Кисть · 2'], ['line', 'Линия · 3'], ['pick', 'Пипетка · E'], ['route', 'Трасса · 4']];
const toolIcons = { select: '↖', pan: '✥', brush: '▱', line: '╱', pick: '◉', route: '⌁' };
const panels = [['properties', 'Свойства'], ['atmos', 'Атмосфера'], ['networks', 'Сети'], ['mirror', 'Зеркало'],
  ['reference', 'Подложка'], ['layers', 'Слои'], ['templates', 'Шаблоны'], ['grid', 'Грид'], ['files', 'Файлы'], ['issues', 'Ошибки']];
const Sprite = ({ images = [], color, cellSize = 32, native = false }) => <>{images.map((image, index) => color ?
  <GridEditorDecalSprite key={index} image={image} color={color} cellSize={cellSize} native={native} /> :
  <img key={index} src={image.url} alt="" draggable={false} style={native && image.native ? {
    left:'50%',top:'50%',width:image.width*cellSize/32,height:image.height*cellSize/32,maxWidth:'none',maxHeight:'none',transformOrigin:'0 0',
    transform:`matrix(${image.m11},${-image.m12},${-image.m21},${image.m22},${image.m31*cellSize},${-image.m32*cellSize}) translate(-50%,-50%)`
  } : undefined} />)}</>;

export const GridEditor = () => {
  const { data, act: sendAct } = useBackend();
  const failureTitle = useRef('Действие не выполнено');
  const act = (action, payload) => {
    if (action === 'applyStart') failureTitle.current = payload?.purpose === 'template' ? 'Не удалось сохранить шаблон' : 'Не удалось применить изменения';
    else if (action === 'fillAtmos') failureTitle.current = 'Не удалось заполнить атмосферу';
    else if (action === 'diagnoseAtmos') failureTitle.current = 'Не удалось проверить герметичность';
    else if (action === 'download') failureTitle.current = payload?.scope === 'grid' ? 'Не удалось сохранить grid' : 'Не удалось сохранить карту';
    else if (action === 'draftFileStart') failureTitle.current = 'Не удалось сохранить черновик grid';
    else if (action === 'favorite') failureTitle.current = 'Не удалось сохранить избранное';
    else if (action === 'hotbarSet') failureTitle.current = 'Не удалось сохранить hotbar';
    else if (action === 'deleteTemplate') failureTitle.current = 'Не удалось удалить шаблон';
    sendAct(action, payload);
  };
  const [mirror, setMirror] = useState({ enabled: false, axis: 'x', center: (data.gridX || 0) + (data.gridWidth || data.width || 16) / 2 });
  const mirrorPairs = useRef(new Map()), selectionRegion = useRef(null);
  const [baseline, setBaseline] = useState(data.items || []);
  const resizeGrid = useRef(null);
  const [expansion, setExpansion] = useState({ left: 0, right: 0, bottom: 0, top: 0 });
  const [localEdits, setLocalEdits] = useState([]);
  const editsRef = useRef([]), patchCursor = useRef(0), serverArea = useRef(null);
  const replaceEdits = edits => { editsRef.current = edits; setLocalEdits(edits); };
  const [draft, setDraft] = useState(data.items || []);
  const [undo, setUndo] = useState([]), [redo, setRedo] = useState([]);
  const [selected, setSelected] = useState([]);
  const [movePreview, setMovePreview] = useState(null);
  const [brush, setBrush] = useState(null), [tool, setTool] = useState('select');
  const [category, setCategory] = useState('tile'), [query, setQuery] = useState('');
  const [catalogOpen, setCatalogOpen] = useState(false), [activePanel, setActivePanel] = useState(null);
  const hotbar = useRef(null);
  const [hotbarSlots, setHotbarSlots] = useState(1), [hotbarDrop, setHotbarDrop] = useState(null);
  const catalogDrag = useRef(null), suppressCatalogClick = useRef(false);
  const [draggedBrush, setDraggedBrush] = useState(null);
  useEffect(() => { catalogDrag.current = null; setDraggedBrush(null); setHotbarDrop(null); }, [data.token]);
  useLayoutEffect(() => {
    const resize = () => setHotbarSlots(Math.max(1, Math.floor((hotbar.current.clientWidth + 5) / 43)));
    const observer = new ResizeObserver(resize); observer.observe(hotbar.current); resize();
    return () => observer.disconnect();
  }, []);
  const [gridNameDraft, setGridNameDraft] = useState(null), [gridNameSaving, setGridNameSaving] = useState(false);
  const gridName = gridNameDraft ?? data.gridName ?? '';
  useEffect(() => { setGridNameDraft(null); setGridNameSaving(false); }, [data.token]);
  useEffect(() => { setGridNameDraft(previous => previous?.trim() === (data.gridName || '') ? null : previous); }, [data.gridName]);
  useEffect(() => {
    setGridNameSaving(false);
    if (data.gridNameError) setFailure({ title: 'Не удалось переименовать grid', message: data.gridNameError });
  }, [data.gridNameResultVersion]);
  const saveGridName = () => {
    if (!gridName.trim() || gridName.trim() === (data.gridName || '') || pending || gridNameSaving) return;
    setGridNameSaving(true); act('setGridName', { name: gridName.trim() });
  };
  const openCatalog = nextCategory => {
    setCatalogOpen(true); setActivePanel(null);
    if (nextCategory) { chooseCategory(nextCategory); setQuery(''); }
  };
  const togglePanel = id => { setActivePanel(previous => previous === id ? null : id); setCatalogOpen(false); };
  useEffect(() => { if (tool === 'route') { setActivePanel('networks'); setCatalogOpen(false); } }, [tool]);
  const [zoom, setZoom] = useState(1), [gridLines, setGridLines] = useState(true);
  const requestedZoom = useRef(1), zoomCenter = useRef(null), settledCamera = useRef(null);
  const [layers, setLayers] = useState({ tile: true, entity: true, decal: true });
  const [rotation, setRotation] = useState(0), [color, setColor] = useState('#ffffffff');
  const [z, setZ] = useState(0), [cleanable, setCleanable] = useState(false);
  const decalMode = category === 'decal';
  const editLayers = useMemo(() => decalMode ? { decal: layers.decal, decalZ: z } : layers, [decalMode, layers, z]);
  const editable = item => editableItem(item, editLayers);
  const [emptyDecalLayers, setEmptyDecalLayers] = useState([0]);
  const decalLayers = [...new Set([...emptyDecalLayers, z, ...draft.filter(item => item.kind === 'decal').map(item => item.z || 0)])].sort((a, b) => a - b);
  const chooseLayer = value => {
    setZ(value); setSelected([]); selectionRegion.current = null; setContextMenu(null);
    gesture.current = null; setMovePreview(null); setRubberband(null); setPreviewCells([]);
  };
  const chooseCategory = id => {
    if (id === 'decal' && !decalMode) route.reset();
    if ((id === 'decal') !== decalMode) {
      setSelected([]); selectionRegion.current = null; setBrush(null); setTemplateBrush(null); setTool('select');
      setContextMenu(null); gesture.current = null; setMovePreview(null); setRubberband(null); setPreviewCells([]);
    }
    setCategory(id);
  };
  const [hover, setHover] = useState(null), [previewCells, setPreviewCells] = useState([]);
  const [rubberband, setRubberband] = useState(null), [contextMenu, setContextMenu] = useState(null), [failure, setFailure] = useState(null);
  const atmosRefresh=useRef(null),atmosQueuedAt=useRef(0),atmosEpoch=useRef(data.token);
  const [atmosFillRequested,setAtmosFillRequested]=useState(false);
  const [atmosMode, setAtmosMode] = useState(false), [atmosBusy, setAtmosBusy] = useState(false);
  const [before, setBefore] = useState(false), [pending, setPending] = useState(false), [confirmClose, setConfirmClose] = useState(false);
  const [sceneProgress, setSceneProgress] = useState({ label: 'Подготовка карты', done: 0, total: 1, blocking: true });
  const [sceneAttempt, setSceneAttempt] = useState(0);
  const [showProgress, setShowProgress] = useState(false);
  const sceneBusy = !!sceneProgress;
  useEffect(() => {
    setShowProgress(false);
    if (!sceneBusy) return;
    const timer = setTimeout(() => setShowProgress(true), 2000);
    return () => clearTimeout(timer);
  }, [sceneBusy, data.token, sceneAttempt]);
  const [clipboard, setClipboard] = useState([]);
  const clipboardRef=useRef([]);
  const [templateBrush, setTemplateBrush] = useState(null), [templateName, setTemplateName] = useState('');
  const viewport = useRef(null), canvas = useRef(null), gesture = useRef(null), current = useRef(draft);
  const root = useRef(null), dividerDrag = useRef(null);
  const [panelWidths, setPanelWidths] = useState({ left: 300, right: 300 });
  const startResize = (side, event) => {
    if (event.button !== 0) return;
    event.preventDefault(); event.stopPropagation();
    dividerDrag.current = { side, x: event.clientX, width: panelWidths[side], previous: { ...panelWidths } };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveResize = event => {
    const active = dividerDrag.current; if (!active) return;
    const delta = (event.clientX - active.x) * (active.side === 'left' ? 1 : -1);
    const maximum = Math.max(180, root.current.clientWidth - 72);
    setPanelWidths(previous => ({ ...previous, [active.side]: Math.max(180, Math.min(maximum, active.width + delta)) }));
  };
  const endResize = (event, cancel = false) => {
    if (cancel && dividerDrag.current) setPanelWidths(dividerDrag.current.previous);
    dividerDrag.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  };
  const divider = side => <div role="separator" aria-label={side === 'left' ? 'Ширина каталога' : 'Ширина свойств'} aria-orientation="vertical"
    className={`GridEditor__divider ${side}`} tabIndex={0} onPointerDown={event => startResize(side, event)} onPointerMove={moveResize}
    onPointerUp={event => endResize(event)} onPointerCancel={event => endResize(event, true)}
    onKeyDown={event => { if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return; event.preventDefault();
      const direction = (event.key === 'ArrowRight' ? 1 : -1) * (side === 'left' ? 1 : -1);
      setPanelWidths(previous => ({ ...previous, [side]: Math.max(180, Math.min(Math.max(180, root.current.clientWidth - 72), previous[side] + direction * 16)) })); }} />;
  useEffect(() => {
    const observer = new ResizeObserver(() => {
      const available = Math.max(180, root.current.clientWidth - 72);
      setPanelWidths(previous => {
        const right = Math.min(previous.right, available);
        const left = Math.min(previous.left, available);
        return left === previous.left && right === previous.right ? previous : { left, right };
      });
    });
    observer.observe(root.current); return () => observer.disconnect();
  }, []);
  const [view, setView] = useState({ left: 0, top: 0, width: 1, height: 1 });
  const measureView = () => {
    const node = viewport.current; if (!node) return;
    const bounds = node.getBoundingClientRect(), plan = canvas.current.getBoundingClientRect();
    const left = bounds.left - plan.left, top = bounds.top - plan.top;
    setView(previous => previous.left === left && previous.top === top && previous.width === node.clientWidth && previous.height === node.clientHeight ? previous :
      { left, top, width: node.clientWidth, height: node.clientHeight });
  };
  useEffect(() => {
    const observer = new ResizeObserver(measureView); observer.observe(viewport.current); measureView();
    return () => observer.disconnect();
  }, []);
  const counter = useRef(0), visualCache = useRef(new Map());
  const upload = useRef(null);
  const draftFile = useRef(null);
  const [exporting, setExporting] = useState(false);
  const space = useRef(false);
  const deleteHold = useRef(false), [deleteHeld, setDeleteHeld] = useState(false);
  const selectHold = useRef(false), [selectHeld, setSelectHeld] = useState(false);
  const activeTool = selectHeld || gesture.current?.selectArea || gesture.current?.ids ? 'select' : tool;
  const keyboardPan = useRef({ keys: new Set(), shift: false, frame: null, time: 0 });
  const stopKeyboardPan = () => {
    const pan = keyboardPan.current; pan.keys.clear();
    if (pan.frame !== null) cancelAnimationFrame(pan.frame);
    pan.frame = null; pan.time = 0;
  };
  const panFrame = time => {
    const pan = keyboardPan.current; pan.frame = null;
    if (!pan.keys.size || gesture.current || document.hidden || document.activeElement?.closest('input:not([type=checkbox]),textarea,select,[contenteditable=true],[role=separator],[role=combobox]')) { stopKeyboardPan(); return; }
    const dx = Number(pan.keys.has('ArrowRight')) - Number(pan.keys.has('ArrowLeft'));
    const dy = Number(pan.keys.has('ArrowDown')) - Number(pan.keys.has('ArrowUp'));
    const distance = Math.min(50, Math.max(0, time - pan.time)) / 1000 * 600 * (pan.shift ? 4 : 1);
    pan.time = time;
    const length = Math.hypot(dx, dy) || 1;
    viewport.current.scrollLeft += dx / length * distance;
    viewport.current.scrollTop += dy / length * distance;
    pan.frame = requestAnimationFrame(panFrame);
  };
  useEffect(() => { stopKeyboardPan(); }, [data.token, failure, contextMenu, confirmClose]);
  useEffect(() => () => stopKeyboardPan(), []);
  const lastPointer = useRef(null), editingLocked = useRef(false);
  const shiftHeld = useRef(false), alphaMasks = useRef(new Map()), [pointer, setPointer] = useState(null);
  const [shift, setShift] = useState(false), [panning, setPanning] = useState(false);
  const [catalogRows, setCatalogRows] = useState([]), [catalogScroll, setCatalogScroll] = useState(0);
  const catalogView = useRef(null), requestedPages = useRef(new Set()), catalogRequest = useRef(null);
  const catalogStart = Math.max(0, Math.floor(catalogScroll / 64) - 2);
  const catalogEnd = Math.min(data.catalogCount || 0, catalogStart + 20);
  const baseArea = useMemo(() => ({ x: data.x || 0, y: data.y || 0, width: data.width || 16, height: data.height || 16 }), [data.x, data.y, data.width, data.height]);
  const area = useMemo(() => {
    const shared = { ...expansion };
    for (const peer of data.peerDrafts || []) for (const side of ['left', 'right', 'bottom', 'top']) {
      const value = peer.expansion?.[side] || 0;
      if (value) shared[side] = shared[side] ? Math.max(shared[side], value) : value;
    }
    return { x: baseArea.x - shared.left, y: baseArea.y - shared.bottom,
      width: Math.max(1, baseArea.width + shared.left + shared.right), height: Math.max(1, baseArea.height + shared.bottom + shared.top) };
  }, [baseArea, expansion, data.peerDrafts]);
  const previousArea = useRef(area);
  useLayoutEffect(() => {
    const previous = previousArea.current;
    if (viewport.current) {
      viewport.current.scrollLeft += (previous.x - area.x) * cellSize;
      viewport.current.scrollTop += (area.y + area.height - previous.y - previous.height) * cellSize;
    }
    previousArea.current = area; measureView();
  }, [area]);
  const gridBounds = { x: data.gridX || 0, y: data.gridY || 0, width: data.gridWidth || 0, height: data.gridHeight || 0 };
  const cellSize = 32 * zoom;
  const itemChanges = localEdits.length;
  const changes = itemChanges + (Object.values(expansion).some(Boolean) ? 1 : 0);
  const validationIssues = useMemo(() => draftIssues(draft, area, new Set(localEdits.map(item => item.id))), [draft, area, localEdits]);
  const exportIssues = data.exportIssues || [];
  const issues = useMemo(() => [...validationIssues, ...exportIssues], [validationIssues, data.exportIssues]);
  const errors = validationIssues[0]?.message || '';
  const showIssue = issue => {
    setFailure(null); setActivePanel('issues'); setCatalogOpen(false);
    if (!issue.located || !viewport.current) return;
    setSelected(draft.some(item => item.id === issue.id) ? [issue.id] : []);
    viewport.current.scrollLeft = (issue.x + .5 - area.x) * cellSize;
    viewport.current.scrollTop = (area.y + area.height - issue.y - .5) * cellSize;
  };
  const locked = before || pending || gridNameSaving || data.downloading || exporting || sceneProgress?.blocking;
  editingLocked.current=locked;
  const atmosCells = data.atmosCells || [];
  const atmosIndex = useMemo(() => new Map(atmosCells.map(cell => [`${cell.x}:${cell.y}`, cell])), [data.atmosCells]);
  const atmosCurrent = data.atmosToken === data.token && data.atmosPatchVersion === (data.patchVersion || 0) && !changes && !pending;
  const hoverAtmos = hover && data.atmosToken === data.token ? atmosIndex.get(`${hover.x}:${hover.y}`) : null;
  const atmosLeaks = useMemo(() => atmosCells.filter(cell => cell.leaks), [data.atmosCells]);
  const requestAtmos = (fill = false) => { clearTimeout(atmosRefresh.current);atmosQueuedAt.current=0;setAtmosFillRequested(fill);setAtmosBusy(true); act(fill ? 'fillAtmos' : 'diagnoseAtmos', {}); };
  // Keep the previous successful overlay visible while a new diagnosis is pending.
  // Coalesce rapid edits/world patches; diagnostics always inspect the applied map.
  useEffect(() => {
    if(atmosEpoch.current!==data.token){atmosEpoch.current=data.token;atmosQueuedAt.current=0;}
    if (!atmosMode || pending) {atmosQueuedAt.current=0;return;}
    const now=performance.now();if(!atmosQueuedAt.current)atmosQueuedAt.current=now;
    // Under continuous activity still refresh at least every two seconds.
    const delay=Math.min(750,Math.max(0,2000-(now-atmosQueuedAt.current)));
    atmosRefresh.current=setTimeout(()=>requestAtmos(),delay);
    return ()=>clearTimeout(atmosRefresh.current);
  }, [atmosMode, pending, data.token, data.patchVersion]);
  useEffect(() => setAtmosBusy(false), [data.atmosVersion, data.resultVersion]);
  const reference = useGridEditorReference({ data, act, area, viewport, canvas, cellSize, blocked: selectHeld || deleteHeld || panning || before || pending, onFailure: message => setFailure({ title: 'Не удалось вставить скриншот', message }) });
  const allDisplayed = before ? baseline : draft;
  const displayed = allDisplayed; // Network visibility is applied to the scene below.
  const visuals = useMemo(() => {
    for (const entry of [...(data.visuals || []), ...(data.patchVisuals || []), ...(data.peerVisuals || []), ...(data.libraryVisuals || []), ...(data.routeVisuals || []), ...(data.catalog || [])])
      visualCache.current.set(`${entry.kind}:${entry.id}`, { ...visualCache.current.get(`${entry.kind}:${entry.id}`), ...entry });
    return new Map(visualCache.current);
  }, [data.visuals, data.patchVisuals, data.peerVisuals, data.libraryVisuals, data.routeVisuals, data.catalog]);
  const visual = (item) => visuals.get(`${item.kind}:${item.prototype}`);
  const spatialIndex = useMemo(() => indexItems(displayed,visual), [displayed,visuals]);
  useEffect(() => {
    for (const entry of visualCache.current.values()) for (const { url } of entry.images || []) {
      if (!url || alphaMasks.current.has(url)) continue;
      alphaMasks.current.set(url, null);
      const image = new Image();
      image.onload = () => {
        try { const buffer = document.createElement('canvas'); buffer.width = image.width; buffer.height = image.height;
          const context = buffer.getContext('2d'); context.drawImage(image, 0, 0);
          alphaMasks.current.set(url, { width: image.width, height: image.height, bytes: context.getImageData(0, 0, image.width, image.height).data });
        } catch { /* Missing masks fall back to the visible sprite bounds. */ }
      };
      image.src = url;
    }
  }, [data.visuals, data.patchVisuals, data.peerVisuals, data.catalog, data.libraryVisuals]);
  const opaque = (images, u, v) => !images.length || images.some(({ url, native }) => {
    const mask = alphaMasks.current.get(url); if (!mask) return true;
    if(native)return mask.bytes[(Math.floor(v*mask.height)*mask.width+Math.floor(u*mask.width))*4+3]>16;
    const scale = Math.max(mask.width, mask.height), x = (u - .5) * scale + mask.width / 2, y = (v - .5) * scale + mask.height / 2;
    return x >= 0 && y >= 0 && x < mask.width && y < mask.height && mask.bytes[(Math.floor(y) * mask.width + Math.floor(x)) * 4 + 3] > 16;
  });
  const pointAt = event => { const rect = canvas.current.getBoundingClientRect(); return { x: area.x + (event.clientX - rect.left) / cellSize, y: area.y + area.height - (event.clientY - rect.top) / cellSize }; };
  const underPointer = point => itemsAtPoint(nearbyItems(spatialIndex, point), point, editLayers, visual, opaque).filter(item=>route.visible || !routeProfile(item));
  const nextId = () => `new_${data.token}_${Date.now()}_${counter.current++}`;
  const update = (next, restore = false, duplicate = true) => {
    if (restore) { current.current = next; setDraft(next); return; }
    const previous = gesture.current?.ids ? gesture.current.original : current.current;
    if (duplicate) next = mirrorChanges(previous, next, mirror, area, nextId, mirrorPairs.current, id => !!visuals.get(`entity:${id}`)?.diagonal);
    next = replaceConflicts(previous, next, id => !!visuals.get(`entity:${id}`)?.exclusive || visuals.get(`entity:${id}`)?.category === 'wall');
    if (decalMode) next = scopeDecalChanges(previous, next, z);
    const supported = decalMode ? next : ensureWallFloors(next, id => visuals.get(`entity:${id}`)?.category === 'wall', baseline, id => !!visuals.get(`entity:${id}`)?.diagonal, current.current);
    replaceEdits(trackLocalEdits(current.current, supported, editsRef.current));
    current.current = supported; setDraft(supported);
  };
  const remember = (previous = current.current, edits = editsRef.current) => { setUndo((history) => [...history.slice(-49), { edits: copyItems(edits), expansion: { ...expansion }, scope: decalMode ? z : null }]); setRedo([]); };
  const change = (next, duplicate = true) => { if (JSON.stringify(next) === JSON.stringify(current.current)) return; remember(); update(next, false, duplicate); };
  const route = useGridEditorRoutes({token:data.token, act, items:draft, area, nextId, locked, toCell:event=>at(event),canvas,
    commit:result=>{change(result.items,false);setSelected([]);},fail:message=>setFailure({title:'Не удалось проложить трассу',message})});
  const sharedEdits=useMemo(()=>{
    if(!route.editing)return localEdits;
    try {
      let serial=0;const preview=route.previewItems(()=>`new_route_${data.token}_${serial++}`);
      const result=new Map(localEdits.map(item=>[item.id,item]));
      for(const item of makePatch(draft,preview.items))result.set(item.id,item);
      return [...result.values()];
    } catch { return localEdits; }
  },[route.graph,route.profile.id,route.capped,localEdits,draft,data.token]);
  const peerDrafts = useGridEditorPeers({ data, act, edits: sharedEdits, visuals, expansion });
  useEffect(() => {
    const merged = collaborativeDraft(baseline, data.peerDrafts || [], editsRef.current,
      id => !!visuals.get(`entity:${id}`)?.exclusive || visuals.get(`entity:${id}`)?.category === 'wall');
    update(merged, true);
    if (gesture.current?.original) gesture.current.original = copyItems(merged);
  }, [data.peerDrafts, baseline]);
  useInterfaceStyleGuard(changes > 0 || route.editing || pending || exporting || data.downloading);
  const sceneItems = useMemo(()=>route.visible ? displayed : displayed.filter(item=>!routeProfile(item)),[displayed,route.visible]);
  const sceneIndex = useMemo(()=>indexItems(sceneItems),[sceneItems]);
  const startGridResize = (sides, event) => {
    if (locked || decalMode || event.button !== 0) return;

    event.preventDefault(); event.stopPropagation(); remember();
    resizeGrid.current = { sides, x: event.clientX, y: event.clientY, expansion: { ...expansion }, cellSize };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveGridResize = event => {
    const drag = resizeGrid.current; if (!drag) return;
    event.stopPropagation();
    const dx = Math.round((event.clientX - drag.x) / drag.cellSize), dy = Math.round((event.clientY - drag.y) / drag.cellSize);
    const next = resizeExpansion(baseArea, drag.expansion, drag.sides, dx, dy, current.current);
    setExpansion(next);
  };
  const endGridResize = (event, cancel = false) => {
    event.stopPropagation();
    if (cancel && resizeGrid.current) setExpansion(resizeGrid.current.expansion);
    resizeGrid.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  };
  const search = (page = 0) => act('search', { category, query, page });
  useEffect(() => {
    setUndo([]); setRedo([]); setSelected([]);
    setExpansion({ left: 0, right: 0, bottom: 0, top: 0 });
    setBaseline(data.items || []); replaceEdits([]); patchCursor.current = 0; serverArea.current = baseArea;
    mirrorPairs.current.clear(); update(copyItems(data.items || []), true); setPending(false); setBefore(false);
    gesture.current = null; setMovePreview(null); setPreviewCells([]); setRubberband(null); setContextMenu(null); setFailure(null); upload.current = null;
  }, [data.token]);
  useEffect(() => {
    if (!data.patchVersion || data.patchVersion <= patchCursor.current) return;
    const batches = data.patchBatches || [{ version: data.patchVersion, applied: !!data.patchApplied,
      items: data.patchItems || [], removed: data.patchRemoved || [], ...baseArea }];
    let next = new Map(baseline.map(item => [item.id, item]));
    let edits = editsRef.current, nextExpansion = expansion, previousArea = serverArea.current || baseArea;
    let history = undo, future = redo, applied = false;
    for (const batch of batches) {
      if (batch.version <= patchCursor.current) continue;
      edits = discardSuperseded(edits, batch.superseded || []);
      const discard = snapshots => snapshots.map(snapshot => ({ ...snapshot, edits: discardSuperseded(snapshot.edits, batch.superseded || []) }));
      history = discard(history); future = discard(future);
      for (const { id } of batch.removed) next.delete(id);
      for (const item of batch.items) next.set(item.id, item);
      const bounds = { x: batch.x, y: batch.y, width: batch.width, height: batch.height };
      if (batch.applied) {
        edits = []; nextExpansion = { left: 0, right: 0, bottom: 0, top: 0 };
        history = []; future = []; applied = true;
      } else {
        nextExpansion = rebaseExpansion(previousArea, bounds, nextExpansion);
        const rebaseHistory = snapshots => snapshots.map(snapshot => ({ ...snapshot,
          expansion: rebaseExpansion(previousArea, bounds, snapshot.expansion) }));
        history = rebaseHistory(history); future = rebaseHistory(future);
      }
      previousArea = bounds; patchCursor.current = batch.version;
    }
    serverArea.current = previousArea;
    const items = [...next.values()], merged = collaborativeDraft(items, data.peerDrafts || [], edits, id => !!visuals.get(`entity:${id}`)?.exclusive || visuals.get(`entity:${id}`)?.category === 'wall');
    setBaseline(items); replaceEdits(edits); update(merged, true); setExpansion(nextExpansion);
    setUndo(history); setRedo(future); setSelected(ids => ids.filter(id => merged.some(item => item.id === id)));
    if (gesture.current?.original) gesture.current.original = copyItems(merged);
    if (applied) { setPending(false); upload.current = null; }
    act('patchAck', { version: patchCursor.current });
  }, [data.patchVersion]);
  useEffect(() => { setPending(false); if (data.success === false && data.status) setFailure({ title: failureTitle.current, message: data.status }); }, [data.resultVersion]);
  useEffect(() => {
    const transfer = draftFile.current;
    if (transfer && data.draftFileId === transfer.id && data.draftFileNext < transfer.chunks.length)
      act('draftFileChunk', { id: transfer.id, index: data.draftFileNext, text: transfer.chunks[data.draftFileNext] });
  }, [data.draftFileId, data.draftFileNext]);
  useEffect(() => {
    if (draftFile.current?.id !== data.draftFileFinished) return;
    draftFile.current = null; setExporting(false);
  }, [data.draftFileFinished]);
  const downloadDraft = () => {
    if (draftFile.current || data.downloading) return;
    try {
      const yaml = exportGridDraft(current.current, gridBounds, expansion, {
        gridName: data.gridName,
        alwaysPowered: !!data.alwaysPowered, forceGravity: !!data.forceGravity,
        safeAtmosphere: !!data.safeAtmosphere, apartmentProtected: !!data.apartmentProtected,
      });
      const id = `${Date.now()}_${counter.current++}`, chunks = draftFileChunks(yaml);
      draftFile.current = { id, chunks }; setExporting(true);
      act('draftFileStart', { id, length: yaml.length, chunks: chunks.length });
    } catch (error) { setFailure({ title: 'Не удалось сохранить черновик grid', message: error.message }); }
  };
  useEffect(() => {
    const transfer = upload.current;
    if (transfer && data.uploadId === transfer.id && data.uploadNext < transfer.chunks.length)
      act('applyChunk', { id: transfer.id, index: data.uploadNext, items: transfer.chunks[data.uploadNext] });
  }, [data.uploadId, data.uploadNext]);
  const startTransfer = (items, purpose = 'apply', name = '') => {
    const id = `${Date.now()}_${counter.current++}`;
    const chunks = [];
    for (let i = 0; i < items.length; i += 16) chunks.push(items.slice(i, i + 16));
    if (!chunks.length) chunks.push([]);
    upload.current = { id, chunks }; setPending(true);
    act('applyStart', { id, count: items.length, chunks: chunks.length, purpose, name, ...(purpose === 'apply' ? { expansion: { left: baseArea.x - area.x, right: area.x + area.width - baseArea.x - baseArea.width, bottom: baseArea.y - area.y, top: area.y + area.height - baseArea.y - baseArea.height } } : {}) });
  };
  const apply = () => {
    const baselineIds = new Set(baseline.map(i => i.id));
    const normalized = current.current.map(i => i.kind !== 'tile' && !baselineIds.has(i.id) && !i.id.startsWith('new_') ? { ...i, id: nextId() } : i);
    update(normalized);
    startTransfer(editsRef.current);
  };
  useEffect(() => {
    requestedPages.current.clear(); catalogRequest.current = 0; setCatalogRows([]); setCatalogScroll(0);
    if (catalogView.current) catalogView.current.scrollTop = 0;
    const timer = setTimeout(() => { requestedPages.current.add(0); search(); }, 180);
    return () => clearTimeout(timer);
  }, [category, query]);
  useEffect(() => {
    if (category !== 'favorites') return;
    requestedPages.current.clear();
    const page = Math.floor(catalogStart / 48);
    requestedPages.current.add(page); catalogRequest.current = page; search(page);
  }, [data.favorites]);
  useEffect(() => {
    if (data.catalogCategory !== category || data.catalogQuery !== query) return;
    if (catalogRequest.current === data.catalogPage) catalogRequest.current = null;
    setCatalogRows(previous => { const next = [...previous]; (data.catalog || []).forEach((entry, index) => { next[(data.catalogPage || 0) * 48 + index] = entry; }); return next; });
  }, [data.catalog, data.catalogPage, data.catalogCategory, data.catalogQuery]);
  useEffect(() => {
    if (catalogRequest.current !== null || category === 'templates' || data.catalogCategory !== category || data.catalogQuery !== query) return;
    for (let page = Math.floor(catalogStart / 48); page <= Math.floor(Math.max(0, catalogEnd - 1) / 48); page++)
      if (!requestedPages.current.has(page)) { requestedPages.current.add(page); catalogRequest.current = page; search(page); break; }
  }, [catalogStart, catalogEnd, category, query, data.catalogCategory, data.catalogQuery, data.catalog]);
  const at = (event) => {
    const rect = canvas.current.getBoundingClientRect();
    return { x: area.x + Math.floor((event.clientX - rect.left) / cellSize),
      y: area.y + area.height - 1 - Math.floor((event.clientY - rect.top) / cellSize) };
  };
  const options = { rotation, color, z, cleanable, replace: true, isWall: (id) => visualCache.current.get(`entity:${id}`)?.category === 'wall' };
  const paint = (cells) => { if (!brush || decalMode && brush.kind !== 'decal') return; update(stamp(current.current, cells.filter((cell) => inside(cell, area)), brush, options, nextId)); };
  const down = (event) => {
    const mode = selectHold.current ? 'select' : tool;
    if (event.button === 2) {
      event.preventDefault();
      if (['brush', 'line'].includes(mode) && (brush || templateBrush)) {
        if (gesture.current?.original) update(gesture.current.original);
        gesture.current = null; setPreviewCells([]); setRubberband(null); setPanning(false); setContextMenu(null);
        setBrush(null); setTemplateBrush(null); setSelected([]); setTool('select');
        if (canvas.current.hasPointerCapture(event.pointerId)) canvas.current.releasePointerCapture(event.pointerId);
        return;
      }
      if (['select', 'pan'].includes(mode)) {
        if (!selectHold.current) { setBrush(null); setTemplateBrush(null); }
        const cell = at(event); if (!inside(cell, area)) return;
        const entries = (spatialIndex.get(`${cell.x}:${cell.y}`) || []).filter(item => editable(item)).slice().reverse().sort((a, b) => drawOrder(b, visual(b)) - drawOrder(a, visual(a)));
        setHover(cell); setPointer(pointAt(event));
        const bounds = root.current.getBoundingClientRect();
        const pointed = underPointer(pointAt(event));
        const menu = { cell, ids: entries.map(item => item.id), item: pointed[0]?.id || entries[0]?.id,
          x: Math.max(175, Math.min(root.current.clientWidth - 175, event.clientX - bounds.left)),
          y: Math.max(150, Math.min(root.current.clientHeight - 155, event.clientY - bounds.top)) };
        setContextMenu(null);
        gesture.current = { context: menu, start: cell, px: event.clientX, py: event.clientY,
          moveIds: !locked && pointed.some(item => selected.includes(item.id)) ? selected : null };
        act('focus'); canvas.current.focus(); canvas.current.setPointerCapture(event.pointerId); return;
      }
      return;
    }
    setContextMenu(null);
    if (event.button === 1 || event.button === 0 && (space.current || mode === 'pan') && !deleteHold.current) {
      event.preventDefault(); act('focus'); canvas.current.focus(); setPanning(true);
      gesture.current = { pan: true, px: event.clientX, py: event.clientY, left: viewport.current.scrollLeft, top: viewport.current.scrollTop };
      canvas.current.setPointerCapture(event.pointerId); return;
    }
    if (locked || event.button !== 0) return;
    if (!decalMode && mode === 'route' && !selectHold.current && !deleteHold.current && !space.current) {
      event.preventDefault();act('focus');canvas.current.focus();const cell=at(event);if(inside(cell,area))route.edit(cell);return;
    }
    event.preventDefault(); act('focus'); canvas.current.focus();
    const cell = at(event); if (!inside(cell, area)) return;
    canvas.current.setPointerCapture(event.pointerId);
    if (deleteHold.current) {
      gesture.current = { start: cell, last: cell, original: copyItems(current.current), erasing: true, clicked: underPointer(pointAt(event))[0]?.id };
      setRubberband(cellRectangle(cell, cell)); return;
    }
    const heldShift = event.shiftKey || shiftHeld.current;
    const pointed = underPointer(pointAt(event));
    const item = mode === 'select' && !heldShift && !selectHold.current ? pointed.find(item => selected.includes(item.id)) || pointed[0] : pointed[0];
    if (heldShift && item && mode !== 'select') {
      if (item) { setSelected(ids => ids.includes(item.id) ? ids.filter(id => id !== item.id) : [...ids, item.id]); if (!selectHold.current) setTool('select'); }
      return;
    }
    if (mode === 'pick') {
      if (item) {
        const definition = visual(item);
        setBrush({ ...definition, id: item.prototype, kind: item.kind, name: definition?.name || item.prototype,
          category: definition?.category || item.kind, anchored: item.anchored });
        setRotation(item.rotation); setColor(item.color || '#ffffffff'); setZ(item.z); setCleanable(item.cleanable); setTool('brush');
        setTemplateBrush(null);
      }
      return;
    }
    if (mode === 'select') {
      if(selectHold.current && item) copySelection([item]);
      selectionRegion.current = null;
      const ids = item ? heldShift ? selected.includes(item.id) ? selected.filter(id => id !== item.id) : [...selected, item.id] : [item.id] : heldShift ? selected : [];
      setSelected(ids);
      if (item?.kind === 'decal') { setColor(item.color || '#ffffffff'); setZ(item.z); setCleanable(item.cleanable); }
      gesture.current = { selectArea: true, start: cell, px: event.clientX, py: event.clientY,
        originalSelected: selected, additive: heldShift ? selected : [] };
      return;
    }
    if (templateBrush && tool === 'brush') {
      const templateItems = decalMode ? decalTemplate(templateBrush.items, z) : templateBrush.items;
      const projected = projectTemplate(templateItems, cell, rotation / 90);
      if (projected.some((item) => !inside(item, area))) return;
      change(insertTemplate(current.current, templateItems, cell, rotation / 90, nextId)); return;
    }
    if (!brush) return;
    gesture.current = { start: cell, last: cell, original: copyItems(current.current), drawing: true };
    if (tool === 'brush') setRubberband(cellRectangle(cell, cell));
    if (tool === 'line') setPreviewCells([cell]);
  };
  const move = (event) => {
    const active = gesture.current;
    if (active?.pan) {
      viewport.current.scrollLeft = active.left - (event.clientX - active.px);
      viewport.current.scrollTop = active.top - (event.clientY - active.py); return;
    }
    const cell = at(event);
    setHover(previous => inside(cell, area) ? previous?.x === cell.x && previous?.y === cell.y ? previous : cell : null);
    const point=pointAt(event);lastPointer.current=point;setPointer(point);
    if (locked || !active) return;
    if (active.context) {
      if (Math.hypot(event.clientX - active.px, event.clientY - active.py) < 6) return;
      active.contextMoved = true;
      if (!active.moveIds) return;
      active.ids = active.moveIds; active.original = copyItems(current.current);
      const cells = selectedItems.map(item => ({ x: Math.floor(item.x), y: Math.floor(item.y) }));
      active.bounds = cells.reduce((bounds, cell) => ({ left: Math.min(bounds.left, cell.x), right: Math.max(bounds.right, cell.x),
        bottom: Math.min(bounds.bottom, cell.y), top: Math.max(bounds.top, cell.y) }), { left: Infinity, right: -Infinity, bottom: Infinity, top: -Infinity });
      active.context = null;
    }
    if (active.selectArea && !active.selecting) {
      if (Math.hypot(event.clientX - active.px, event.clientY - active.py) < 6) return;
      active.selecting = true;
    }
    const bounded = { x: Math.max(area.x, Math.min(area.x + area.width - 1, cell.x)), y: Math.max(area.y, Math.min(area.y + area.height - 1, cell.y)) };
    if (active.lastMove?.x === bounded.x && active.lastMove?.y === bounded.y) return;
    active.lastMove = bounded;
    if (active.erasing) {
      active.last = bounded; setRubberband(cellRectangle(active.start, bounded));
    } else if (active.selectArea) {
      const rectangle = cellRectangle(active.start, bounded);
      setRubberband(rectangle);
      setSelected([...new Set([...active.additive, ...current.current.filter((item) => editable(item) &&
        inside({ x: Math.floor(item.x), y: Math.floor(item.y) }, rectangle)).map((item) => item.id)])]);
    } else if (active.ids) {
      // Preview the offset only. Rebuilding/validating a large draft on every
      // mouse movement defeats the scene cache; commit once on pointer release.
      const dx = Math.max(area.x - active.bounds.left, Math.min(bounded.x - active.start.x, area.x + area.width - 1 - active.bounds.right));
      const dy = Math.max(area.y - active.bounds.bottom, Math.min(bounded.y - active.start.y, area.y + area.height - 1 - active.bounds.top));
      active.offset = { dx, dy }; setMovePreview(active.offset);
    }
    else if (active.drawing) {
      if (tool === 'brush') setRubberband(cellRectangle(active.start, bounded));
      if (tool === 'line') setPreviewCells(cellsBetween(active.start, bounded));
      active.last = bounded;
    }
  };
  const up = (event, cancel = false) => {
    const active = gesture.current; if (!active) return;
    if (active.context) {
      if (!cancel) move(event);
      if (active.context && !cancel && !active.contextMoved) setContextMenu(active.context);
    }
    if (active.context) { /* A click opens the menu; a drag never also opens it. */ }
    else if (active.selectArea) {
      if (cancel) setSelected(active.originalSelected);
      else {
        move(event);
        if (active.selecting && !active.additive.length) {
          const rectangle = cellRectangle(active.start, active.lastMove || active.start);
          selectionRegion.current = { rectangle, ids: current.current.filter(item => editable(item) && inside({x:Math.floor(item.x),y:Math.floor(item.y)},rectangle)).map(item=>item.id) };
        }
      }
    } else if (!active.pan) {
      if (cancel) { update(active.original, true); if (active.ids) setSelected(active.ids); }
      else {
        const previousEdits = copyItems(editsRef.current);
        move(event);
        if (active.ids && active.offset) {
          const moved = moveSelection(active.original, active.ids, active.offset.dx, active.offset.dy, area);
          update(moved.items); setSelected(moved.ids);
        }
        if (active.erasing) update(active.start.x === active.last.x && active.start.y === active.last.y ? active.original.filter(item => item.id !== active.clicked) : eraseRectangle(active.original, cellRectangle(active.start, active.last), editLayers, mirror));
        if (active.drawing && tool === 'brush') paint(cellsBetween(active.start, active.last, true));
        if (active.drawing && tool === 'line') paint(cellsBetween(active.start, active.last));
        if (JSON.stringify(current.current) !== JSON.stringify(active.original)) remember(active.original, previousEdits);
      }
    }
    gesture.current = null; setMovePreview(null); setPreviewCells([]); setRubberband(null); setPanning(false);
    if (canvas.current.hasPointerCapture(event.pointerId)) canvas.current.releasePointerCapture(event.pointerId);
  };
  const step = (forward) => {
    const history = forward ? redo : undo; if (locked) return;
    const index = history.findLastIndex(entry => (entry.scope ?? null) === (decalMode ? z : null));
    if (index < 0) return;
    const snapshot = history[index];
    const next = collaborativeDraft(baseline, data.peerDrafts || [], snapshot.edits || [], id => !!visuals.get(`entity:${id}`)?.exclusive || visuals.get(`entity:${id}`)?.category === 'wall');
    const previous = { edits: copyItems(editsRef.current), expansion: { ...expansion }, scope: decalMode ? z : null };
    if (forward) { setUndo((h) => [...h, previous]); setRedo(history.filter((_, i) => i !== index)); }
    else { setRedo((h) => [...h, previous]); setUndo(history.filter((_, i) => i !== index)); }
    const scoped = decalMode ? scopeDecalChanges(current.current, next, z) : next;
    replaceEdits(decalMode ? trackLocalEdits(current.current, scoped, editsRef.current) : copyItems(snapshot.edits || []));
    update(scoped, true); if (!decalMode) setExpansion(snapshot.expansion); setSelected([]);
  };
  const remove = () => {
    const region = selectionRegion.current;
    const rectangle = region && region.ids.length === selected.length && region.ids.every(id => selected.includes(id)) ? region.rectangle : null;
    change(rectangle ? eraseRectangle(current.current, rectangle, editLayers, mirror) : current.current.filter(i => !selected.includes(i.id)));
    setSelected([]); selectionRegion.current = null;
  };
  const reflect = (axis = 'x') => {
    if (!selected.length || locked) return;
    const region = selectionRegion.current;
    const rectangle = region && region.ids.length === selected.length && region.ids.every(id => selected.includes(id)) &&
      current.current.filter(item => selected.includes(item.id)).every(item => inside({x:Math.floor(item.x),y:Math.floor(item.y)},region.rectangle)) ? region.rectangle : null;
    const result = reflectSelection(current.current, selected, axis, id => !!visuals.get(`entity:${id}`)?.diagonal, rectangle);
    if (rectangle) selectionRegion.current = { rectangle, ids: result.ids };
    change(result.items, false); setSelected(result.ids);
  };
  const rotate = () => {
    if (selected.length && activeTool === 'select') change(current.current.map((i) => selected.includes(i.id) && i.kind !== 'tile' ? { ...i, rotation: (i.rotation + 90) % 360 } : i));
    else setRotation((r) => (r + 90) % 360);
  };
  const copySelection = items => { if (!items.length) return; clipboardRef.current=copyItems(items);setClipboard(clipboardRef.current); act('copySelection'); };
  const paste = () => {
    const copied=decalMode ? decalTemplate(clipboardRef.current, z) : clipboardRef.current;
    if (!copied.length) return;
    const origin = { x: Math.min(...copied.map((i) => Math.floor(i.x))), y: Math.min(...copied.map((i) => Math.floor(i.y))) };
    const location=lastPointer.current;
    const target=location&&inside(location,area)?{x:Math.floor(location.x),y:Math.floor(location.y)}:hover||{x:origin.x+1,y:origin.y+1};
    const added = projectTemplate(copied, target);
    if (added.some((i) => !inside(i, area))) return;
    change(insertTemplate(current.current, copied, target, 0, nextId)); setSelected([]);
  };
  const menuItems = contextMenu ? contextMenu.ids.map(id => current.current.find(item => item.id === id)).filter(Boolean) : [];
  const menuItem = contextMenu ? menuItems.find(item => item.id === contextMenu.item) : null;
  const menuTargets = menuItem ? selected.includes(menuItem.id) ? current.current.filter(item => selected.includes(item.id)) : [menuItem] : [];
  const menuAction = action => {
    if (locked || !contextMenu) return;
    const targets = menuTargets;
    if (action === 'select' && menuItem) { setSelected([menuItem.id]); setTool('select'); }
    if (action === 'copy' && targets.length) { copySelection(targets); setSelected(targets.map(item => item.id)); setTool('select'); }
    if (action === 'duplicate') {
      const source = targets.length ? targets : clipboard;
      const copied = decalMode ? decalTemplate(source, z) : source;
      if (copied.length) {
        const target = { x: contextMenu.cell.x + (targets.length ? 1 : 0), y: contextMenu.cell.y };
        const template = makeTemplate(copied);
        if (projectTemplate(template, target).some(item => !inside(item, area))) setFailure({ title: 'Не удалось вставить постройку', message: 'Постройка выходит за границы плана. Выберите другое место для вставки.' });
        else change(insertTemplate(current.current, template, target, 0, nextId));
      }
    }
    if (action === 'rotate') { const ids = new Set(targets.map(item => item.id)); change(current.current.map(item => ids.has(item.id) && item.kind !== 'tile' ? { ...item, rotation: (item.rotation + 90) % 360 } : item)); }
    if (action === 'delete') { const ids = new Set(targets.map(item => item.id)); change(current.current.filter(item => !ids.has(item.id))); setSelected([]); }
    if (action === 'pick' && menuItem) {
      const definition = visual(menuItem);
      setBrush({ ...definition, id: menuItem.prototype, kind: menuItem.kind, name: definition?.name || menuItem.prototype, category: definition?.category || menuItem.kind, anchored: menuItem.anchored });
      setTemplateBrush(null); setRotation(menuItem.rotation); setColor(menuItem.color || '#ffffffff'); setZ(menuItem.z); setCleanable(menuItem.cleanable); setSelected([]); setTool('brush');
    }
    setContextMenu(null);
  };
  useEffect(() => {
    const key = (event) => {
      if (event.ctrlKey && event.code === 'KeyS') { event.preventDefault(); downloadDraft(); return; }
      if (event.target?.closest('input:not([type=checkbox]),textarea,select,[contenteditable=true],[role=separator],[role=combobox]')) return;
      if (failure || contextMenu || confirmClose) {
        if (editorKey(event) === 'escape') { event.preventDefault(); if (failure) setFailure(null); else if (contextMenu) setContextMenu(null); else setConfirmClose(false); }
        return;
      }
      const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.code || event.key];
      if (direction && !event.ctrlKey && !event.altKey) {
        event.preventDefault();
        if (!gesture.current) {
          const pan = keyboardPan.current; pan.keys.add(event.code || event.key); pan.shift = !!event.shiftKey;
          if (pan.frame === null) { pan.time = performance.now(); pan.frame = requestAnimationFrame(panFrame); }
          setHover(null); setPointer(null);
        }
        return;
      }
      if (event.code === 'KeyD' && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault();
        if (!event.repeat && !locked && !gesture.current) {
          if (tool==='route' && route.editing) route.removePoint();
          else if (selected.length) remove();
          else { deleteHold.current = true; setDeleteHeld(true); }
        }
        return;
      }
      if (event.code === 'KeyS' && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault();if(!editingLocked.current&&!gesture.current&&!event.repeat){
          selectHold.current=true;setSelectHeld(true);setTool('select');
          const location=lastPointer.current;const item=location&&inside(location,area)?underPointer(location)[0]:null;
          if(item){setSelected([item.id]);selectionRegion.current=null;copySelection([item]);}
        }return;
      }
      if (event.code === 'Space') { event.preventDefault(); space.current = true; setPanning(true); return; }
      if (editorKey(event) === 'shift') { keyboardPan.current.shift = true; shiftHeld.current = true; setShift(true); }
      if (locked || gesture.current) return;
      const key = editorKey(event);
      if (event.ctrlKey && key === 'v' && !event.nativeBridge) reference.imagePasteOnly.current = false;
      if (event.ctrlKey && key === 'z') { event.preventDefault(); if(tool==='route'&&(event.shiftKey?route.canRedo:route.canUndo))route.undo(event.shiftKey);else step(event.shiftKey); }
      else if (event.ctrlKey && key === 'y') { event.preventDefault(); if(tool==='route'&&route.canRedo)route.undo(true);else step(true); }
      else if (event.ctrlKey && key === 'c') { event.preventDefault(); copySelection(current.current.filter((i) => selected.includes(i.id))); }
      else if (event.ctrlKey && key === 'v' && event.nativeBridge) { event.preventDefault(); paste(); }
      else if ((event.code==='Enter'||key==='enter')&&tool==='route'&&route.editing) {event.preventDefault();route.finish();}
      else if (key === 'delete') { event.preventDefault(); remove(); }
      else if (key === 'm' && !event.ctrlKey && !event.altKey && !event.metaKey) { event.preventDefault(); if (!event.repeat) reflect(event.shiftKey ? 'y' : 'x'); }
      else if (key === 'r') { event.preventDefault(); rotate(); }
      else if (key === 'escape') { route.reset(); setContextMenu(null); setSelected([]); setTool('select'); setBrush(null); setTemplateBrush(null); }
      else if (!event.ctrlKey && !event.altKey) {
        const shortcuts = { 1: 'pan', 2: 'brush', 3: 'line', ...(!decalMode && { 4: 'route' }), e: 'pick' };
        if (shortcuts[key]) { event.preventDefault(); setTool(shortcuts[key]); }
      }
    };
    const release = event => { if (event.code === 'KeyS') { selectHold.current = false; setSelectHeld(false); } if (event.code === 'KeyD') { deleteHold.current = false; setDeleteHeld(false); } keyboardPan.current.keys.delete(event.code || event.key); if (!keyboardPan.current.keys.size) stopKeyboardPan(); if (event.code === 'Space') { space.current = false; setPanning(false); } if (editorKey(event) === 'shift') { keyboardPan.current.shift = false; shiftHeld.current = false; setShift(false); } };
    const blur = () => { selectHold.current = false; setSelectHeld(false); deleteHold.current = false; setDeleteHeld(false); stopKeyboardPan(); keyboardPan.current.shift = false; space.current = false; shiftHeld.current = false; setPanning(false); setShift(false); };
    const nativeKey = event => { const input = { ...event.detail, nativeBridge: true, target: document.activeElement, preventDefault() {} }; if (input.down) key(input); else release(input); };
    document.addEventListener('keydown', key, true); document.addEventListener('keyup', release, true); window.addEventListener('blur', blur); window.addEventListener('grid-editor-key', nativeKey);
    return () => { document.removeEventListener('keydown', key, true); document.removeEventListener('keyup', release, true); window.removeEventListener('blur', blur); window.removeEventListener('grid-editor-key', nativeKey); };
  }, [draft, selected, locked, tool, selectHeld, undo, redo, hover, clipboard, failure, contextMenu, confirmClose, mirror, area, zoom, layers,
    exporting, data.downloading, data.gridName, data.alwaysPowered, data.forceGravity, data.safeAtmosphere, data.apartmentProtected, decalMode, z, editLayers, expansion, data.gridX, data.gridY, data.gridWidth, data.gridHeight, pointer, route.graph, route.canUndo, route.canRedo, route.visible]);
  const restoreZoomCenter = () => {
    const center = zoomCenter.current; if (!center) return;
    const node = viewport.current;
    // Half a viewport of padding lets even the plan's edges stay at the camera center.
    node.scrollLeft = center.x * cellSize;
    node.scrollTop = center.y * cellSize;
    // Keep the precise anchor across wheel events: integer scroll rounding at a
    // tiny scale must not accumulate into a large jump when zooming back in.
    settledCamera.current = { center, left: node.scrollLeft, top: node.scrollTop, width: node.clientWidth, height: node.clientHeight };
    zoomCenter.current = null;
  };
  const setScale = (value, center) => {
    const node = viewport.current, bounds = node.getBoundingClientRect(), plan = canvas.current.getBoundingClientRect();
    const saved = settledCamera.current;
    const unchanged = saved && saved.left === node.scrollLeft && saved.top === node.scrollTop && saved.width === node.clientWidth && saved.height === node.clientHeight;
    // Capture the camera before the browser clamps scroll offsets to the smaller plan.
    zoomCenter.current = center || zoomCenter.current || (unchanged ? saved.center : null) || {
      x: (bounds.left + node.clientWidth / 2 - plan.left) / cellSize,
      y: (bounds.top + node.clientHeight / 2 - plan.top) / cellSize,
    };
    const next = Math.max(0.001, Math.min(4, value));
    requestedZoom.current = next;
    if (next === zoom) {
      restoreZoomCenter(); measureView();
    }
    setZoom(next); setHover(null); setPointer(null);
  };
  useLayoutEffect(() => {
    restoreZoomCenter();
    measureView();
  }, [zoom, view.width, view.height]);
  const fit = () => setScale(Math.min((viewport.current.clientWidth - 32) / (area.width * 32), (viewport.current.clientHeight - 32) / (area.height * 32)),
    { x: area.width / 2, y: area.height / 2 });
  useEffect(() => { const frame = requestAnimationFrame(fit); return () => cancelAnimationFrame(frame); }, [data.grid, data.x, data.y, data.width, data.height]);
  const wheel = (event) => {
    event.preventDefault();
    if (gesture.current) return;
    setScale(requestedZoom.current * (event.deltaY < 0 ? 1.15 : 1 / 1.15));
  };
  useEffect(() => {
    const view = viewport.current;
    view.addEventListener('wheel', wheel, { passive: false });
    return () => view.removeEventListener('wheel', wheel);
  }, [zoom]);
  const pointedItems = useMemo(() => pointer ? underPointer(pointer) : [], [pointer, spatialIndex, editLayers, visuals, route.visible]);
  const hoveredItem = (shift || ['pick', 'select'].includes(activeTool)) ? pointedItems[0] : null;
  const selectedItems = useMemo(() => { const ids = new Set(selected); return displayed.filter(item => ids.has(item.id) && (route.visible || !routeProfile(item))); }, [displayed, selected, route.visible]);
  const highlightedItems=useMemo(()=>!movePreview&&hoveredItem&&!selected.includes(hoveredItem.id)?[...selectedItems,hoveredItem]:selectedItems,[selectedItems,hoveredItem,selected,movePreview]);
  const one = selectedItems[0];
  const patchSelected = (patch) => change(current.current.map((i) => selected.includes(i.id) && i.kind !== 'tile' ? { ...i, ...patch } : i));
  const favorite = (kind, id) => (data.favorites || []).some((f) => f.kind === kind && f.id === id);
  const chooseBrush = entry => {
    if ((entry.kind === 'decal') !== decalMode) chooseCategory(entry.kind === 'decal' ? 'decal' : entry.category || entry.kind);
    setBrush(entry); setTemplateBrush(null); setTool('brush'); setSelected([]); setCatalogOpen(false);
    if (entry.kind === 'decal') setActivePanel('layers');
  };
  const hotbarAt = event => {
    const slot = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-hotbar-slot]');
    return slot ? Number(slot.dataset.hotbarSlot) : null;
  };
  const startBrushDrag = (event, entry) => {
    if (locked || event.button !== 0) return;
    catalogDrag.current = { entry, x: event.clientX, y: event.clientY, pointer: event.pointerId, source: event.currentTarget, moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveBrushDrag = event => {
    const drag = catalogDrag.current;
    if (!drag || drag.pointer !== event.pointerId) return;
    if (!drag.moved && Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 5) return;
    event.preventDefault(); event.stopPropagation(); drag.moved = true;
    setDraggedBrush({ entry: drag.entry, x: event.clientX, y: event.clientY }); setHotbarDrop(hotbarAt(event));
  };
  const endBrushDrag = (event, cancelled = false) => {
    const drag = catalogDrag.current;
    if (!drag || drag.pointer !== event.pointerId) return;
    catalogDrag.current = null; setDraggedBrush(null); setHotbarDrop(null);
    if (drag.source.hasPointerCapture(event.pointerId)) drag.source.releasePointerCapture(event.pointerId);
    if (!drag.moved) return;
    event.preventDefault(); event.stopPropagation(); suppressCatalogClick.current = true;
    setTimeout(() => { suppressCatalogClick.current = false; }, 0);
    const slot = hotbarAt(event);
    if (!cancelled && !locked && slot !== null) act('hotbarSet', { slot, kind: drag.entry.kind, id: drag.entry.id });
  };
  const templatePreview = templateBrush && hover && activeTool === 'brush' ? projectTemplate(templateBrush.items, hover, rotation / 90) : [];
  const close = () => changes || route.editing ? setConfirmClose(true) : act('close');
  useEffect(() => { if (data.closeRequested) close(); }, [data.closeRequested]);
  return <div ref={root} className="GridEditor" style={{ '--catalog-width': `${panelWidths.left}px`, '--properties-width': `${panelWidths.right}px` }} onPointerDownCapture={() => act('focus')}
    onPointerMoveCapture={moveBrushDrag} onPointerUpCapture={event => endBrushDrag(event)} onPointerCancelCapture={event => endBrushDrag(event, true)}
    onClickCapture={event => { if (suppressCatalogClick.current) { event.preventDefault(); event.stopPropagation(); suppressCatalogClick.current = false; } }}
    onPasteCapture={event => {
      if (event.target.closest('input,textarea,select,[contenteditable=true]')) return;
      const imageOnly = reference.imagePasteOnly.current; reference.imagePasteOnly.current = false;
      if (!reference.handlePaste(event)) { event.preventDefault(); if (imageOnly) setFailure({ title: 'Не удалось вставить скриншот', message: 'В буфере обмена нет изображения.' }); else paste(); }
    }}
    onDragOver={event => { if (event.dataTransfer.types.includes('Files')) event.preventDefault(); }}
    onDrop={event => { if (event.dataTransfer.files.length) { event.preventDefault(); reference.importFile(event.dataTransfer.files[0]); } }}
    onFocusCapture={e => act('editing', { active: !!e.target.closest('input:not([type=checkbox]),textarea,select,[contenteditable=true]') })}
    onBlurCapture={e => act('editing', { active: !!e.relatedTarget?.closest('input:not([type=checkbox]),textarea,select,[contenteditable=true]') })}>
    <header><div className="GridEditor__identity"><strong><span className="GridEditor__gridName" title={data.gridName || 'Редактор grid'}>{data.gridName || 'Редактор grid'}</span><small>Фокус · #{data.grid} · {area.width}×{area.height}</small></strong>
      {data.sourcePath && <code className="GridEditor__sourcePath" title={data.sourcePath}>{data.sourcePath}</code>}</div>
      <span className="GridEditor__draftStatus" role="status">{pending ? 'Применение к grid…' : before ? 'Исходный grid' : `${changes} изменений в черновике`}</span><button aria-label="Закрыть" onClick={close} disabled={pending}>×</button></header>
    <div className="GridEditor__commandBar">
      <button className={catalogOpen ? 'active' : ''} aria-expanded={catalogOpen} aria-controls="grid-editor-catalog" onClick={() => catalogOpen ? setCatalogOpen(false) : openCatalog()}>Каталог</button>
      <button className="GridEditor__currentBrush" onClick={() => openCatalog()} title={brush?.id || templateBrush?.name || 'Открыть каталог'}>
        {brush && <span className="GridEditor__sprite"><Sprite images={brush.images} color={brush.kind === 'decal' ? color : undefined} /></span>}
        <span>{templateBrush?.name || brush?.name || brush?.id || 'Выберите прототип'}</span>
      </button><span className="GridEditor__spacer" />
      <button aria-label="Отменить · Ctrl+Z" title="Отменить · Ctrl+Z" disabled={!undo.length || locked} onClick={() => step(false)}>↶</button>
      <button aria-label="Повторить · Ctrl+Shift+Z" title="Повторить · Ctrl+Shift+Z" disabled={!redo.length || locked} onClick={() => step(true)}>↷</button>
      <button disabled={pending} className={before ? 'active' : ''} aria-pressed={before} onClick={() => setBefore(!before)}>До / После</button>
    </div>
    <nav className="GridEditor__sections" aria-label="Разделы редактора">{panels.map(([id, name]) => <button key={id}
      className={activePanel === id || id === 'atmos' && atmosMode ? 'active' : ''} disabled={id === 'networks' && decalMode}
      aria-expanded={activePanel === id} aria-controls="grid-editor-properties" onClick={() => { togglePanel(id); if (id === 'atmos') setAtmosMode(true); }}>{name}</button>)}</nav>
    <div className="GridEditor__body">
      <nav className="GridEditor__toolRail" aria-label="Инструменты">{[['select', 'Выделение · S'], ...tools].filter(([id]) => !decalMode || id !== 'route').map(([id, name]) =>
        <button key={id} aria-label={name} title={name} aria-pressed={activeTool === id} disabled={locked || !!templateBrush && id === 'line'}
          className={activeTool === id ? 'active' : ''} onClick={() => setTool(id)}><span aria-hidden="true">{toolIcons[id]}</span><small>{id === 'select' ? 'S' : name.split(' · ')[1]}</small></button>)}
        <button aria-label="Повернуть · R" title="Повернуть · R" disabled={locked} onClick={rotate}>↻<small>R</small></button>
      </nav>
      <aside id="grid-editor-catalog" className="GridEditor__catalog" hidden={!catalogOpen} aria-label="Каталог прототипов">
        <div className="GridEditor__panelHeading"><strong>Каталог</strong><button aria-label="Закрыть каталог" onClick={() => setCatalogOpen(false)}>×</button></div>
        <nav>{categories.map(([id, name]) => <button key={id} className={category === id ? 'active' : ''} onClick={() => chooseCategory(id)}>{name}</button>)}</nav>
        <input aria-label="Поиск прототипов" placeholder="Название или ID прототипа…" value={query} onFocus={() => act('focus')} onChange={(e) => setQuery(e.target.value)} />
        <div ref={catalogView} className="GridEditor__entries" onScroll={e => setCatalogScroll(e.currentTarget.scrollTop)}>{category === 'templates' ? (data.templates || []).filter((t) => t.name.toLowerCase().includes(query.toLowerCase())).map((entry) =>
          <div key={entry.id} className="GridEditor__entry"><button disabled={locked} className={templateBrush?.id === entry.id ? 'active' : ''}
            onClick={() => { setTemplateBrush(entry); setBrush(null); setTool('brush'); setSelected([]); setRotation(0); setCatalogOpen(false); }}><span>{entry.name}<small>{entry.items.length} элементов</small></span></button>
            <button aria-label={`Удалить шаблон ${entry.name}`} disabled={locked} onClick={() => setConfirmClose({ deleteTemplate: entry.id, name: entry.name })}>×</button></div>) : <div style={{ height: (data.catalogCount || 0) * 64, position: 'relative' }}>{Array.from({ length: Math.max(0, catalogEnd - catalogStart) }, (_, offset) => { const index = catalogStart + offset; const entry = catalogRows[index]; return entry ? <div style={{ position: 'absolute', top: index * 64, height: 64, width: '100%' }} key={`${entry.kind}:${entry.id}`} className="GridEditor__entry"><button disabled={locked} title={entry.id}
          draggable={false} onPointerDown={event => startBrushDrag(event, entry)} className={brush?.id === entry.id && brush?.kind === entry.kind ? 'active' : ''} onClick={() => chooseBrush(entry)}>
          <span className="GridEditor__sprite"><Sprite images={entry.images} color={entry.kind === 'decal' ? color : undefined} /></span><span>{entry.name}<small>{entry.id}</small></span></button>
          <button aria-label={`${favorite(entry.kind, entry.id) ? 'Убрать из' : 'Добавить в'} избранное ${entry.name}`} disabled={locked}
            onClick={() => act('favorite', { kind: entry.kind, id: entry.id })}>{favorite(entry.kind, entry.id) ? '★' : '☆'}</button></div> : <div key={index} style={{ position: 'absolute', top: index * 64 }}>Загрузка…</div>; })}</div>}</div>
        <div className="GridEditor__pages">{data.catalogCount || 0} элементов · прокрутите каталог</div>
      </aside>
      {catalogOpen && divider('left')}
      <main>
        {sceneProgress && (showProgress || sceneProgress.error) && <div className={`GridEditor__loading ${sceneProgress.blocking ? 'blocking' : ''}`} role="status">
          <div>{sceneProgress.error ? <><strong>Не удалось подготовить карту</strong><span>{sceneProgress.error}</span>
            <button onClick={() => setSceneAttempt(value => value + 1)}>Повторить</button></> : <>
            <span>{sceneProgress.label} · {Math.round(100 * sceneProgress.done / Math.max(1, sceneProgress.total))}%</span>
            <progress aria-label={sceneProgress.label} value={sceneProgress.done} max={Math.max(1, sceneProgress.total)} />
          </>}</div>
        </div>}
        <div ref={viewport} className="GridEditor__viewport" onScroll={measureView} onContextMenu={(e) => e.preventDefault()}>
          <div className="GridEditor__surface" style={{ width: area.width * cellSize + view.width, height: area.height * cellSize + view.height }}
            onPointerDown={down} onPointerMove={move} onPointerUp={(e) => up(e)} onPointerCancel={(e) => up(e, true)} onPointerLeave={() => { if (!gesture.current) {setHover(null);setPointer(null);lastPointer.current=null;} }}>
          <div ref={canvas} className={`GridEditor__canvas ${gridLines ? 'grid' : ''} ${panning ? 'panning' : ''} tool-${deleteHeld ? 'erase' : activeTool}`} tabIndex={0} aria-label="План grid"
            style={{ left: view.width / 2, top: view.height / 2, width: area.width * cellSize, height: area.height * cellSize, '--cell': `${cellSize}px` }}>
            {!decalMode && !before && [['left'], ['right'], ['top'], ['bottom'], ['left', 'top'], ['right', 'top'], ['left', 'bottom'], ['right', 'bottom']].map(sides =>
              <button key={sides.join('-')} aria-label={`Растянуть grid: ${sides.join(' ')}`} disabled={locked}
                className={`GridEditor__resizeGrid ${sides.join(' ')}`} onPointerDown={event => startGridResize(sides, event)}
                onPointerMove={moveGridResize} onPointerUp={event => endGridResize(event)} onPointerCancel={event => endGridResize(event, true)} />)}
            <GridEditorScene key={`${data.token}:${sceneAttempt}`} index={sceneIndex} area={area} layers={layers} visuals={visuals} view={view} cellSize={cellSize} onProgress={setSceneProgress} />
            {!before && !!peerDrafts.peers.length && <GridEditorPeers items={[]} removed={peerDrafts.removed} area={area} view={view} cellSize={cellSize} visuals={visuals} />}
            {atmosMode && data.atmosToken === data.token && !before && <GridEditorAtmos cells={atmosCells} area={area} view={view} cellSize={cellSize} />}
            {mirror.enabled && <div className={`GridEditor__mirrorLine ${mirror.axis}`} style={mirror.axis === 'x' ? { left: (mirror.center - area.x) * cellSize, height: area.height * cellSize } : { top: (area.y + area.height - mirror.center) * cellSize, width: area.width * cellSize }} />}
            {reference.overlay}
            {rubberband && <div className={`GridEditor__rubberband ${(deleteHeld || gesture.current?.erasing) ? 'erase' : ''}`} style={{
              left: (rubberband.x - area.x) * cellSize, top: (area.y + area.height - rubberband.y - rubberband.height) * cellSize,
              width: rubberband.width * cellSize, height: rubberband.height * cellSize }} />}
            {!decalMode && route.visible && layers.entity && <GridEditorNetworks items={allDisplayed} area={area} view={view} cellSize={cellSize}/>}
            {!decalMode && !before && tool==='route' && <GridEditorRouteHandles route={route} area={area} view={view} cellSize={cellSize} locked={locked}/>}
            <GridEditorSelection items={highlightedItems} visuals={visuals} area={area} layers={layers} view={view} cellSize={cellSize} offset={movePreview} />
            {!before && <GridEditorIssues issues={issues} area={area} view={view} cellSize={cellSize} />}
            {((deleteHeld || gesture.current?.erasing || ['brush','line'].includes(activeTool)) ? (previewCells.length ? previewCells : hover ? [hover] : []) : []).map((cell) => <div key={`${cell.x}:${cell.y}`} className={`GridEditor__cursor ${(deleteHeld || gesture.current?.erasing) ? 'erase' : ''}`}
              style={{ left: (cell.x - area.x) * cellSize, top: (area.y + area.height - 1 - cell.y) * cellSize, width: cellSize, height: cellSize }}>
              {brush && ['brush', 'line'].includes(activeTool) && <span style={{ transform: `rotate(${brush?.noRotation ? 0 : -rotation}deg)` }}><Sprite images={brush.images} native cellSize={cellSize} color={brush.kind === 'decal' ? color : undefined} /></span>}</div>)}
            {templatePreview.map((item, index) => <div key={`template_${index}`} className="GridEditor__cursor"
              style={{ left: (item.x - (item.kind === 'entity' ? .5 : 0) - area.x) * cellSize,
                top: (area.y + area.height - 1 - item.y + (item.kind === 'entity' ? .5 : 0)) * cellSize, width: cellSize, height: cellSize }}>
              <span style={{ transform: `rotate(${visual(item)?.noRotation ? 0 : -item.rotation}deg)` }}><Sprite images={visual(item)?.images} native cellSize={cellSize} color={item.kind === 'decal' ? item.color : undefined} /></span></div>)}
          </div>
          </div>
        </div>
      </main>
      {activePanel && divider('right')}
      <aside id="grid-editor-properties" className="GridEditor__properties" hidden={!activePanel} aria-label="Панель настроек">
        <div className="GridEditor__panelHeading"><strong>{panels.find(([id]) => id === activePanel)?.[1]}</strong><button aria-label="Закрыть панель" onClick={() => setActivePanel(null)}>×</button></div>
        <section className="GridEditor__panelSection GridEditor__issuesList" hidden={activePanel !== 'issues'}>
          <strong>Ошибки: {issues.length}</strong>
          {!!exportIssues.length && <small>Ошибка последнего экспорта. После исправления повторите сохранение.</small>}
          {issues.map((issue, index) => <button key={index} onClick={() => showIssue(issue)}>
            <strong>⚠ {issue.name || issue.prototype || issue.id}</strong>
            {issue.prototype && <span>{issue.prototype}</span>}
            <span>{issue.id}{issue.located ? ` · (${Math.floor(issue.x)}, ${Math.floor(issue.y)})` : ' · вне текущего grid или без координат'}</span>
            {issue.component && <span>Компонент: {issue.component}</span>}
            <span>{issue.message}</span>
          </button>)}
        </section>
        <section className="GridEditor__panelSection" hidden={activePanel !== 'layers'}>
        {!decalMode && <button disabled={locked} onClick={() => chooseCategory('decal')}>Режим декалирования</button>}
        {decalMode && <section className="GridEditor__decalLayers" aria-label="Слои декалей">
          <strong>Режим декалирования</strong>
          {decalLayers.map(layer => <button key={layer} className={z === layer ? 'active' : ''} disabled={locked} onClick={() => chooseLayer(layer)}>
            Слой {layer} · {draft.filter(item => item.kind === 'decal' && (item.z || 0) === layer).length}</button>)}
          <button disabled={locked || decalLayers.length >= 201} onClick={() => {
            const value = Array.from({ length: 201 }, (_, i) => i <= 100 ? i : 100 - i).find(value => !decalLayers.includes(value));
            if (value !== undefined) { setEmptyDecalLayers(values => [...values, value]); chooseLayer(value); }
          }}>Добавить слой декалей</button>
          <button disabled={locked} onClick={() => { const items = current.current.filter(editable); setSelected(items.map(item => item.id)); if (items.length === 1) { setColor(items[0].color); setRotation(items[0].rotation); setCleanable(items[0].cleanable); } setTool('select'); selectionRegion.current = null; }}>Выделить слой</button>
          <button disabled={locked} onClick={() => { change(current.current.filter(item => !editable(item))); setSelected([]); selectionRegion.current = null; }}>Удалить слой</button>
        </section>}
        {(decalMode || brush?.kind === 'decal' || one?.kind === 'decal') && <>
          <label>Цвет <input aria-label="Цвет декали" type="color" value={color.slice(0, 7)} disabled={locked} onChange={(e) => { const value = e.target.value + (color.slice(7) || 'ff'); setColor(value); if (one?.kind === 'decal') patchSelected({ color: value }); }} /></label>
          <label>Непрозрачность <input aria-label="Непрозрачность декали" type="range" min="0" max="255" value={parseInt(color.slice(7) || 'ff', 16)} disabled={locked} onChange={e => {
            const value = color.slice(0, 7) + Number(e.target.value).toString(16).padStart(2, '0'); setColor(value);
            if (one?.kind === 'decal') patchSelected({ color: value });
          }} /></label>
          <label>Слой декали <input aria-label="Слой декали" type="number" min={-100} max={100} value={z} disabled={locked} onChange={(e) => { const value = Math.max(-100, Math.min(100, Number(e.target.value))); if (decalMode) chooseLayer(value); else { setZ(value); if (one?.kind === 'decal') patchSelected({ z: value }); } }} /></label>
          <label><input type="checkbox" checked={cleanable} disabled={locked} onChange={(e) => { setCleanable(e.target.checked); if (one?.kind === 'decal') patchSelected({ cleanable: e.target.checked }); }} />Можно смыть</label>
        </>}
        </section>
        <section className="GridEditor__panelSection" hidden={activePanel !== 'properties'}>
        {!!pointedItems.length && <><strong>Под курсором</strong>{pointedItems.slice(0, 8).map(item => <button key={item.id} disabled={locked} onPointerDown={e => e.preventDefault()} onClick={() => {
          if (tool === 'pick') { const definition = visual(item); setBrush({ ...definition, id: item.prototype, kind: item.kind, anchored: item.anchored }); setRotation(item.rotation); setColor(item.color || '#ffffffff'); setZ(item.z); setCleanable(item.cleanable); setTemplateBrush(null); setTool('brush'); }
          else { setSelected([item.id]); setTool('select'); }
        }}>{visual(item)?.name || item.prototype}</button>)}</>}
        </section>
        <section className="GridEditor__panelSection" data-panel="networks" hidden={activePanel !== 'networks'}>
        {!decalMode && <><strong>Провода и трубы</strong>{route.controls}</>}
        {!decalMode&&tool!=='route'&&<button disabled={locked} onClick={()=>setTool('route')}>Редактировать трассы</button>}
        </section>
        <section className="GridEditor__panelSection" data-panel="mirror" hidden={activePanel !== 'mirror'}>
        <strong>Зеркальный маппинг</strong>
        <label><input type="checkbox" checked={mirror.enabled} disabled={locked} onChange={event => { mirrorPairs.current.clear(); setMirror({ ...mirror, enabled: event.target.checked }); }} />Дублировать правки</label>
        <GridEditorSelect aria-label="Ось зеркальности" value={mirror.axis} disabled={locked}
          options={[{ value: 'x', label: 'Вертикальная ось' }, { value: 'y', label: 'Горизонтальная ось' }]}
          onChange={axis => { mirrorPairs.current.clear(); setMirror({ ...mirror, axis, center: axis === 'x' ? gridBounds.x + gridBounds.width / 2 : gridBounds.y + gridBounds.height / 2 }); }} />
        <label>Координата оси <input aria-label="Координата оси зеркальности" type="number" step={.5} disabled={locked} value={mirror.center} onChange={event => { mirrorPairs.current.clear(); setMirror({ ...mirror, center: Math.round(Number(event.target.value) * 2) / 2 }); }} /></label>
        </section>
        <section className="GridEditor__panelSection" data-panel="reference" hidden={activePanel !== 'reference'}>{reference.controls}</section>
        <section className="GridEditor__panelSection" data-panel="atmos" hidden={activePanel !== 'atmos'}>
        <strong>Атмосфера</strong>
        <label><input type="checkbox" checked={atmosMode} onChange={event => setAtmosMode(event.target.checked)} />Диагностика утечек</label>
        {atmosMode && <>
          {!atmosCurrent && data.atmosToken===data.token && <span>Результат предыдущей проверки</span>}
          <>
            <span>Герметичных клеток: {data.atmosSealed || 0}. С утечкой: {data.atmosLeaking || 0}. Выходов: {atmosLeaks.length}.</span>
            <button disabled={atmosBusy || locked || !!changes} onClick={() => requestAtmos()}>{atmosBusy ? 'Проверка…' : 'Проверить герметичность'}</button>
            <button disabled={atmosBusy || locked || !!changes} onClick={() => requestAtmos(true)}>Заполнить герметичные области</button>
            <span>Воздух: 101,3 кПа · 20 °C</span>
            {atmosFillRequested && <small>Заполнено клеток: {data.atmosFilled || 0}</small>}
            {atmosLeaks.slice(0, 8).map(cell => <button key={`${cell.x}:${cell.y}`} onClick={() => {
              viewport.current.scrollLeft = (cell.x + .5 - area.x) * cellSize;
              viewport.current.scrollTop = (area.y + area.height - cell.y - .5) * cellSize;
            }}>Выход наружу: {cell.x}, {cell.y}</button>)}
          </>
          {hoverAtmos && <small>Клетка {hoverAtmos.x}, {hoverAtmos.y}: {{ sealed: 'герметично', leaking: 'есть выход наружу', blocked: 'воздух заблокирован', space: 'космический тайл' }[hoverAtmos.status]}, {hoverAtmos.pressure.toFixed(1)} кПа.</small>}
        </>}

        </section>
        <section className="GridEditor__panelSection" data-panel="layers" hidden={activePanel !== 'layers'}>
        <strong>Видимость</strong>{[['tile', 'Полы'], ['entity', 'Объекты и стены'], ['decal', 'Декали']].map(([id, name]) => <label key={id}><input type="checkbox" checked={layers[id]} onChange={(e) => setLayers({ ...layers, [id]: e.target.checked })} />{name}</label>)}
        </section>
        <section className="GridEditor__panelSection" data-panel="grid" hidden={activePanel !== 'grid'}>
        <label htmlFor="grid-editor-name">Название грида</label>
        <input id="grid-editor-name" aria-label="Название грида" value={gridName} maxLength={data.gridNameMaxLength || 128}
          disabled={pending || gridNameSaving} onChange={event => setGridNameDraft(event.target.value)}
          onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); saveGridName(); } }} />
        <button disabled={pending || gridNameSaving || !gridName.trim() || gridName.trim() === (data.gridName || '')} onClick={saveGridName}>
          {gridNameSaving ? 'Сохранение названия…' : 'Сохранить название'}</button>
        {!decalMode && <strong>Область grid · {area.width}×{area.height}</strong>}
        <button className={data.alwaysPowered ? 'active' : ''} aria-pressed={!!data.alwaysPowered} disabled={pending}
          onClick={() => act('setGridPower', { enabled: !data.alwaysPowered })}>Автономное питание</button>
        <button className={data.forceGravity ? 'active' : ''} aria-pressed={!!data.forceGravity} disabled={pending}
          onClick={() => act('setGridGravity', { enabled: !data.forceGravity })}>Постоянная гравитация</button>
        <button disabled={locked} onClick={() => act('repairConnections', {})}>Починить все соединения</button>
        {!!peerDrafts.peers.length && <><strong>Совместное редактирование</strong>
          {peerDrafts.peers.map(peer => <span key={peer.player}>{peer.name}: {peer.items.length + peer.removedItems.length} правок{peer.expansion && Object.values(peer.expansion).some(Boolean) ? ` · растягивание: ←${peer.expansion.left} →${peer.expansion.right} ↓${peer.expansion.bottom} ↑${peer.expansion.top}` : ''}</span>)}
        </>}
        </section>
        <section className="GridEditor__panelSection" data-panel="properties" hidden={activePanel !== 'properties'}>
        <strong>{selected.length ? `Выбрано: ${selected.length}` : 'Кисть'}</strong><p>{one?.prototype || templateBrush?.name || brush?.name || 'Ничего не выбрано'}</p>
        <button disabled={locked} onClick={rotate}>Повернуть · R ({activeTool === 'select' && one ? one.rotation : rotation}°)</button>

        {one?.kind === 'entity' && <label><input type="checkbox" disabled={locked} checked={one.anchored} onChange={(e) => patchSelected({ anchored: e.target.checked })} />Закреплён</label>}
        {one && one.kind !== 'tile' && selected.length === 1 && ['x', 'y'].map((axis) => <label key={axis}>{axis.toUpperCase()}
          <input type="number" aria-label={`Координата ${axis.toUpperCase()}`} step={0.125} value={one[axis]} disabled={locked}
            onChange={(e) => { const value = Number(e.target.value); if (Number.isFinite(value)) patchSelected({ [axis]: value }); }} /></label>)}
        <button disabled={locked || !selected.length} onClick={() => reflect()}>Отразить · M</button>
        <button disabled={locked || !selected.length} onClick={remove}>Удалить · D / Delete</button>
        <button disabled={locked || !selected.length} onClick={() => copySelection(draft.filter((i) => selected.includes(i.id)))}>Копировать · Ctrl+C</button>
        <button disabled={locked || !clipboard.length} onClick={paste}>Вставить · Ctrl+V</button>
        </section>
        <section className="GridEditor__panelSection" data-panel="templates" hidden={activePanel !== 'templates'}>
        <strong>Свой шаблон</strong><input aria-label="Имя шаблона" value={templateName} maxLength={64} onFocus={() => act('focus')} onChange={(e) => setTemplateName(e.target.value)} />
        <button disabled={locked || !selected.length || selected.length > 512 || !templateName.trim()} onClick={() =>
          startTransfer(makeTemplate(draft.filter((i) => selected.includes(i.id))), 'template', templateName.trim())}>Сохранить выделение</button>
        {(data.templates || []).map(entry => <div key={entry.id} className="GridEditor__entry"><button disabled={locked} className={templateBrush?.id === entry.id ? 'active' : ''}
          onClick={() => { setTemplateBrush(entry); setBrush(null); setTool('brush'); setSelected([]); setRotation(0); setActivePanel(null); }}><span>{entry.name}<small>{entry.items.length} элементов</small></span></button>
          <button aria-label={`Удалить шаблон ${entry.name}`} disabled={locked} onClick={() => setConfirmClose({ deleteTemplate: entry.id, name: entry.name })}>×</button></div>)}
        </section>
        <section className="GridEditor__panelSection" data-panel="files" hidden={activePanel !== 'files'}>
          <button disabled={exporting || data.downloading} onClick={downloadDraft} title="Ctrl+S">{exporting ? 'Сохранение…' : 'Скачать черновик grid'}</button>
          <button disabled={locked || !!changes} onClick={() => act('download', { scope: 'grid' })}>Исходный grid</button>
          <button disabled={locked || !!changes} onClick={() => act('download', { scope: 'map' })}>Скачать карту</button>
        </section>
      </aside>
    </div>
    <div className="GridEditor__viewBar"><span>{hover ? `X ${hover.x} · Y ${hover.y}` : 'Координаты grid'}</span>
      <label><input type="checkbox" checked={gridLines} onChange={event => setGridLines(event.target.checked)} />Сетка</label>
      <details className="GridEditor__help"><summary>Управление</summary><div>S: выбрать и копировать под курсором · S + ЛКМ: выделить область · M: отразить ↔ · Shift+M: отразить ↕ · D: удалить выделение · ПКМ + движение: перенос выделения · D + ЛКМ: удалить объект / область · Стрелки: перемещение · Shift + стрелки: быстрее · Space + ЛКМ / СКМ: перемещение · Клик ПКМ: меню / сброс кисти · Shift + ЛКМ: выбор · Колесо: масштаб</div></details>
      <span className="GridEditor__spacer" /><button aria-label="Уменьшить масштаб" onClick={() => setScale(requestedZoom.current / 1.15)}>−</button><span>{Math.round(zoom * 100)}%</span>
      <button aria-label="Увеличить масштаб" onClick={() => setScale(requestedZoom.current * 1.15)}>+</button><button onClick={fit}>Вписать</button>
    </div>
    <div className="GridEditor__quickAccess" aria-label="Hotbar"><span>Hotbar</span>
      <div ref={hotbar} className="GridEditor__quickEntries">{Array.from({ length: hotbarSlots }, (_, slot) => {
        const saved = (data.hotbar || []).find(entry => entry.slot === slot), entry = saved && visuals.get(`${saved.kind}:${saved.id}`);
        return <button key={slot} data-hotbar-slot={slot} disabled={locked} draggable={false} onPointerDown={event => entry && startBrushDrag(event, entry)}
          aria-label={`Слот ${slot + 1}${saved ? `: ${entry?.name || saved.id}` : ': пусто'}`}
          title={saved ? `${entry?.name || saved.id} · ${saved.id}` : `Слот ${slot + 1}`}
          className={`${entry && brush?.id === entry.id && brush?.kind === entry.kind ? 'active' : ''} ${hotbarDrop === slot ? 'drop' : ''}`}
          onContextMenu={event => { event.preventDefault(); if (!locked && saved) act('hotbarSet', { slot }); }}
          onClick={() => entry ? chooseBrush(entry) : openCatalog()}>
          {entry ? <span className="GridEditor__sprite"><Sprite images={entry.images} color={entry.kind === 'decal' ? color : undefined} /></span> : <small>{slot + 1}</small>}
        </button>;
      })}</div>
      <span className="GridEditor__quickHint">R — поворот</span>
    </div>
    {draggedBrush && <div className="GridEditor__draggedBrush" aria-hidden="true" style={{ left: draggedBrush.x + 12, top: draggedBrush.y + 12 }}>
      <span className="GridEditor__sprite"><Sprite images={draggedBrush.entry.images} color={draggedBrush.entry.kind === 'decal' ? color : undefined} /></span>
      {draggedBrush.entry.name || draggedBrush.entry.id}
    </div>}
    {!!issues.length && <div className="GridEditor__errorBanner" role="alert"><strong>Ошибки: {issues.length}</strong><span>{errors || 'Не удалось сохранить карту'}</span><button onClick={() => showIssue(issues[0])}>Показать на карте</button></div>}
    <footer><span className={errors || data.success === false ? 'error' : ''} role="status">{errors || data.status || ''}</span>
      <button disabled={pending || !changes} onClick={() => { remember(); const next = decalMode ? scopeDecalChanges(current.current, baseline, z) : copyItems(baseline); replaceEdits(decalMode ? trackLocalEdits(current.current, next, editsRef.current) : []); update(next, true); setSelected([]); if (!decalMode) setExpansion({ left: 0, right: 0, bottom: 0, top: 0 }); }}>Сбросить</button>
      <button className="primary" disabled={locked || route.editing || !changes || !!errors} onClick={apply} title={route.editing?'Сначала завершите трассу кнопкой «Трассу в черновик»':undefined}>{pending ? 'Применение…' : 'Применить'}</button></footer>
    {contextMenu && <div className="GridEditor__contextBackdrop" onPointerDown={event => { if (event.target === event.currentTarget) { event.preventDefault(); setContextMenu(null); } }} onContextMenu={event => event.preventDefault()}>
      <div className="GridEditor__radial" role="dialog" aria-label="Меню клетки" style={{ left: contextMenu.x, top: contextMenu.y }}>
        <div className="GridEditor__cellItems"><strong>Клетка {contextMenu.cell.x}, {contextMenu.cell.y}</strong>
          <div>{menuItems.length ? menuItems.map(item => <button key={item.id} className={menuItem?.id === item.id ? 'active' : ''} title={`${item.prototype} · ${item.kind}`} onClick={() => setContextMenu(previous => ({ ...previous, item: item.id }))}>{visual(item)?.name || item.prototype}</button>) : <small>Пустая клетка</small>}</div>
          <button disabled={locked || !menuItems.length} onClick={() => { setSelected(menuItems.map(item => item.id)); setTool('select'); setContextMenu(null); }}>Выбрать всё ({menuItems.length})</button>
        </div>
        {[['select', 'Выбрать'], ['copy', 'Копировать'], ['duplicate', menuItem ? 'Дублировать' : 'Вставить'], ['rotate', 'Повернуть'], ['delete', 'Удалить'], ['pick', 'Пипетка']].map(([action, label], index) => <button key={action} className={`GridEditor__radialAction action-${action}`} disabled={locked || (action === 'duplicate' ? !menuItem && !clipboard.length : !menuItem || action === 'rotate' && menuTargets.every(item => item.kind === 'tile'))}
          style={{ left: `calc(50% + ${Math.cos((index * 60 - 90) * Math.PI / 180) * 132}px)`, top: `calc(50% + ${Math.sin((index * 60 - 90) * Math.PI / 180) * 132}px)` }} onClick={() => menuAction(action)}>{label}</button>)}
      </div>
    </div>}
    {confirmClose && <div className="GridEditor__veil"><div role="dialog" aria-label="Подтверждение редактора"><strong>{confirmClose.deleteTemplate ? `Удалить шаблон «${confirmClose.name}»?` : 'Отказаться от черновика?'}</strong><p>{confirmClose.deleteTemplate ? 'Постройки на карте сохранятся.' : `В черновике ${changes} изменений.`}</p><button onClick={() => setConfirmClose(false)}>Продолжить</button><button onClick={() => {
      if (confirmClose === true) act('close'); else if (confirmClose.deleteTemplate) { act('deleteTemplate', { id: confirmClose.deleteTemplate }); setConfirmClose(false); }
      else { act('region', confirmClose); setConfirmClose(false); }
    }}>{confirmClose.deleteTemplate ? 'Удалить шаблон' : confirmClose === true ? 'Отказаться и закрыть' : 'Отказаться и перейти'}</button></div></div>}
    {failure && <div className="GridEditor__veil GridEditor__failure"><div role="alertdialog" aria-modal="true" aria-label="Ошибка редактора grid"><strong>{failure.title}</strong><p>{failure.message}</p>{!!issues.length && <button onClick={() => showIssue(issues[0])}>Показать ошибку на карте</button>}<button className="primary" autoFocus onClick={() => setFailure(null)}>Понятно</button></div></div>}
  </div>;
};
