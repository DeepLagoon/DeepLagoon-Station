// Text nodes stay text nodes. Never turn server/player strings into executable HTML.
const gameTag = /\[\/?(?:color|font|size|bold|italic|bolditalic|mono|head|align|center|stroke|cmdlink|bullet|br|b|i|u)(?:[=\s][^\]]*)?\s*\/?\]/gi;
const htmlSyntax = /<\/?[a-z][^>]*>|&(?:#\d+|#x[\da-f]+|[a-z]+);/i;
const rawFields = new Set(['html', 'svg', 'source', 'code', 'css', 'imageData', 'spriteData',
  'src', 'icon', 'image', 'preview', 'uri', 'url', 'href', 'sprite', 'texture', 'rsi', 'prototype']);
const htmlTags = new Set(('html head body title meta link style script template div span p br hr a b strong i em u s del ins font small big sub sup ' +
  'h1 h2 h3 h4 h5 h6 code pre blockquote ul ol li dl dt dd table thead tbody tfoot tr td th caption img picture source video audio iframe object embed ' +
  'form input button select option textarea label section article header footer main nav aside figure figcaption details summary svg path').split(' '));

export function displayText(value) {
  let text = value.replace(/\[bullet\s*\/?\]/gi, '• ').replace(/\[br\s*\/?\]/gi, '\n').replace(gameTag, '');
  if (!htmlSyntax.test(text)) return text;
  // DOMParser creates an inert document. Only its text is copied, never any nodes or attributes.
  for (let pass = 0; pass < 2 && htmlSyntax.test(text); pass++) {
    const html = text.replace(/<\/?([a-z][\w-]*)\b[^>]*>/gi, (tag, name) => htmlTags.has(name.toLowerCase()) ? tag : tag.replaceAll('<', '&lt;').replaceAll('>', '&gt;'))
      .replace(/<br\s*\/?>/gi, '\n').replace(/<\/(?:p|div|li|tr|h[1-6])\s*>/gi, '\n');
    const document = new DOMParser().parseFromString(html, 'text/html');
    document.querySelectorAll('script, style, iframe, object, template').forEach(node => node.remove());
    const next = document.body.textContent || '';
    if (next === text) break;
    text = next;
  }
  return text.replace(gameTag, '');
}

/** Normalize presentation strings at the common backend boundary, with structural sharing. */
export function displayData(value, key = '') {
  if (rawFields.has(key)) return value;
  if (typeof value === 'string') return displayText(value);
  if (!value || typeof value !== 'object') return value;
  let changed = false;
  const result = Array.isArray(value) ? [] : {};
  for (const name of Object.keys(value)) {
    const next = displayData(value[name], name);
    result[name] = next;
    changed ||= next !== value[name];
  }
  return changed ? result : value;
}
