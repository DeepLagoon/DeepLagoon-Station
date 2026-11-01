import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const script = await readFile(new URL('../../Resources/Web/DeepLagoon/wiki-tree.js', import.meta.url), 'utf8');
function render(book = false) {
  const nodes = new Map();
  const events = {};
  const classes = new Set();
  const links = [{ attrs: {}, setAttribute(k, v) { this.attrs[k] = v; },
    getAttribute(k) { return this.attrs[k]; }, hasAttribute(k) { return k in this.attrs; },
    removeAttribute(k) { delete this.attrs[k]; } }];
  const window = { __deepLagoonWikiConfig: { root: '/ru/in_game', locale: 'ru', book } };
  const document = {
    body: { classList: {
      add: name => classes.add(name), remove: name => classes.delete(name),
      toggle(name, enabled) { enabled ? classes.add(name) : classes.delete(name); },
    } },
    head: { append: node => nodes.set(node.id, node) },
    getElementById: id => nodes.get(id), createElement: tag => ({ tag }),
    querySelectorAll: () => links,
    querySelector: () => null,
    addEventListener: (name, fn) => { assert.equal(events[name], undefined); events[name] = fn; },
  };
  const run = () => vm.runInNewContext(script, { window, document });
  run();
  return { nodes, events, classes, links, window, run };
}

test('ordinary help keeps links in the same window without a second sidebar', () => {
  const state = render();
  state.run();
  assert.equal(state.nodes.size, 1);
  assert.equal(state.nodes.has('dl-game-wiki-tree'), false);
  assert.equal(state.nodes.has('dl-game-wiki-toggle'), false);
  assert.equal(state.links[0].attrs.target, '_self');
  assert.equal(state.classes.has('dl-game-book'), false);
});

test('books suppress mouse, keyboard click and middle-click navigation', () => {
  const state = render(true);
  assert.equal(state.classes.has('dl-game-book'), true);
  assert.equal(state.links[0].attrs['aria-disabled'], 'true');
  assert.equal(state.links[0].attrs.tabindex, '-1');
  for (const type of ['click', 'auxclick']) {
    let prevented = 0;
    state.events[type]({ target: { closest: () => ({}) }, preventDefault() { prevented++; }, stopImmediatePropagation() {} });
    assert.equal(prevented, 1);
  }
});

test('click guard reads current mode so ordinary help works after reading a book', () => {
  const state = render(true);
  state.window.__deepLagoonWikiConfig.book = false;
  state.run();
  assert.equal(state.classes.has('dl-game-book'), false);
  let prevented = false;
  assert.equal(state.links[0].attrs['aria-disabled'], undefined);
  assert.equal(state.links[0].attrs.tabindex, undefined);
  state.events.click({ target: { closest: () => ({}) }, preventDefault() { prevented = true; } });
  assert.equal(prevented, false);
});
