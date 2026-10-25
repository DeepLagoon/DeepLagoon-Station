// Earlier XML exports indented ordinary prose, which Markdown turned into code.
export function repairProseBlocks(content, renderMarkdown) {
  const decode = value => value.replace(/&(?:amp|lt|gt|quot|apos|#39|#\d+|#x[0-9a-f]+);/gi, entity => {
    const names = {'&amp;':'&', '&lt;':'<', '&gt;':'>', '&quot;':'"', '&apos;':"'", '&#39;':"'"};
    if (names[entity.toLowerCase()]) return names[entity.toLowerCase()];
    const numeric = entity.slice(2, -1);
    return String.fromCodePoint(numeric[0].toLowerCase() === 'x' ? parseInt(numeric.slice(1), 16) : Number(numeric));
  });
  for (let pass = 0; pass < 5; pass++) {
    let changed = false;
    content = content.replace(/<pre>\s*<code(\s[^>]*)?>([\s\S]*?)<\/code>\s*<\/pre>/gi, (whole, attrs, encoded) => {
      // Language-labelled examples are intentional code; preserve them.
      if (attrs && /language-/.test(attrs)) return whole;
      let text = decode(encoded);
      if (!/[А-Яа-яЁё]/.test(text) || /^\s*(?:using |import |function |class |def |<\?php|\{)/.test(text)) return whole;
      // Nested legacy code blocks may already contain unescaped HTML.
      text = text.replace(/<\/?(?:pre|code)\b[^>]*>/gi, '');
      const lines = text.split('\n');
      const indent = Math.min(...lines.filter(line => line.trim()).map(line => /^ */.exec(line)[0].length));
      text = lines.map(line => line.slice(Number.isFinite(indent) ? indent : 0)).join('\n');
      changed = true;
      return '<div class="dl-prose-fragment">' + renderMarkdown(text) + '</div>';
    });
    if (!changed) break;
  }
  return content;
}
