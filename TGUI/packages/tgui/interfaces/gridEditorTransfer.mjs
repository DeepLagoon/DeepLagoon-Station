// Native scene state may exceed the 8192-character CEF action limit even for one item.
export function gridEditorTransfer(items) {
  const text = JSON.stringify(items);
  if (text.length > 32 * 1024 * 1024) throw Error('Сцена превышает 32 МиБ. Перенесите её несколькими выделениями.');
  const encoding = items.some(item => item.entityData) || items.some((_, index) => index % 16 === 0 && JSON.stringify(items.slice(index, index + 16)).length > 7000) ? 'json' : '';
  const chunks = [];
  if (encoding) {
    for (let offset = 0; offset < text.length;) {
      let end = Math.min(offset + 1024, text.length);
      if (end < text.length && text.charCodeAt(end - 1) >= 0xD800 && text.charCodeAt(end - 1) <= 0xDBFF) end--;
      chunks.push(text.slice(offset, end)); offset = end;
    }
  } else for (let offset = 0; offset < items.length; offset += 16) chunks.push(items.slice(offset, offset + 16));
  if (!chunks.length) chunks.push([]);
  return { encoding, chunks };
}
