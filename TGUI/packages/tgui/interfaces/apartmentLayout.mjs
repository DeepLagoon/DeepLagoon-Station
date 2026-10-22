export const copyLayout = (layout) => layout.map((item) => ({ ...item }));

export const footprint = (item, definition) => {
  const width = item.rotation % 2 ? definition.height : definition.width;
  const height = item.rotation % 2 ? definition.width : definition.height;
  const cells = [];
  for (let x = 0; x < width; x++) for (let y = 0; y < height; y++) cells.push([item.x + x, item.y + y]);
  return cells;
};

// A local hint only; the server validates the submitted plan independently.
export const validateLayout = (layout, room) => {
  const errors = {};
  const occupied = new Map();
  const blocked = new Set();
  const counts = {};
  const ids = new Set();
  const floors = room.scene ? new Set(room.scene.tiles.map(cell => `${cell.x},${cell.y}`)) : null;
  const fixed = new Set((room.scene?.occupied || []).map(cell => `${cell.x},${cell.y}`));
  const staticBlocked = new Set((room.scene?.blocked || []).map(cell => `${cell.x},${cell.y}`));
  const mapped = room.catalog.filter(def => def.existingOnly);
  if (mapped.some(def => !layout.some(item => item.id === def.placementId && item.furniture === def.id))) errors.general = 'Исходную обстановку нельзя удалять или заменять.';
  if (layout.filter(item => !mapped.some(def => def.placementId === item.id)).length > room.maxFurniture) errors.general = 'Превышен лимит мебели.';
  for (const item of layout) {
    const def = room.catalog.find((entry) => entry.id === item.furniture);
    if (!def) { errors[item.id] = 'Предмет отсутствует в каталоге.'; continue; }
    if (def.existingOnly && item.id !== def.placementId || mapped.some(entry => entry.placementId === item.id && entry.id !== item.furniture)) errors[item.id] = 'Исходную обстановку нельзя копировать или заменять.';
    if (ids.has(item.id)) errors[item.id] = 'Повторяющийся предмет.';
    ids.add(item.id);
    if (![item.x, item.y, item.rotation].every(Number.isInteger) || item.rotation < 0 || item.rotation > 3) {
      errors[item.id] = 'Некорректное положение.'; continue;
    }
    if (def.canRotate === false && item.rotation !== 0) errors[item.id] = 'Этот предмет нельзя поворачивать.';
    counts[item.furniture] = (counts[item.furniture] || 0) + 1;
    if (counts[item.furniture] > def.maxCount) errors[item.id] = 'Недостаточно доступных экземпляров.';
    for (const [x, y] of footprint(item, def)) {
      const key = `${x},${y}`;
      if ((room.reservedCells || []).some(cell => cell.x === x && cell.y === y)) errors[item.id] = 'Площадку лифта и коридор нельзя занимать мебелью.';
      if (floors ? !floors.has(key) : x < 1 || y < 1 || x >= room.width - 1 || y >= room.height - 1) errors[item.id] = 'Предмет пересекается со стеной.';
      if (fixed.has(key)) errors[item.id] = 'Место занято стационарным объектом квартиры.';
      if (x === room.arrivalX && (y === room.arrivalY || !room.scene && y === room.arrivalY + 1)) errors[item.id] = 'Площадка прибытия и терминал должны оставаться свободными.';
      if (!def.allowsOverlap && occupied.has(key)) {
        errors[item.id] = errors[occupied.get(key)] = 'Предметы пересекаются.';
      }
      if (!def.allowsOverlap) occupied.set(key, item.id);
      if (def.blocksMovement) blocked.add(key);
    }
  }
  const reach = (obstacles) => {
    const reachable = new Set([`${room.arrivalX},${room.arrivalY}`]);
    const queue = [[room.arrivalX, room.arrivalY]];
    for (let index = 0; index < queue.length; index++) {
      const [x, y] = queue[index];
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const nx = x + dx, ny = y + dy, key = `${nx},${ny}`;
        if (floors ? !floors.has(key) : nx < 1 || ny < 1 || nx >= room.width - 1 || ny >= room.height - 1) continue;
        if (obstacles.has(key) || staticBlocked.has(key) || reachable.has(key)) continue;
        reachable.add(key); queue.push([nx, ny]);
      }
    }
    return reachable;
  };
  const reachable = reach(blocked);
  const baselineBlocked = new Set();
  for (const def of mapped.filter(def => def.blocksMovement))
    for (const [x, y] of footprint({ x: def.originalX, y: def.originalY, rotation: def.originalRotation }, def)) baselineBlocked.add(`${x},${y}`);
  for (const key of reach(baselineBlocked)) {
    if (!blocked.has(key) && !reachable.has(key)) errors.general = 'Мебель перекрывает проход к выходу.';
  }
  return errors;
};
