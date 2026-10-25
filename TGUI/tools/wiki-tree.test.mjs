import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const script = await readFile(new URL('../../Resources/Web/DeepLagoon/wiki-tree.js', import.meta.url), 'utf8');

async function render(config, pathname, pages) {
  const created = [];
  const requests = [];
  const element = (tag) => {
    const node = {
      tag, style: {}, children: [],
      attributes: {},
      classList: {
        values: new Set(),
        add(...values) { values.forEach(value => this.values.add(value)); },
        remove(...values) { values.forEach(value => this.values.delete(value)); },
        toggle(value) { if (this.values.delete(value)) return false; this.values.add(value); return true; },
      },
      setAttribute(name, value) { this.attributes[name] = value; },
      append(...children) { this.children.push(...children); children.forEach(child => { child.parent = this; }); },
      replaceChildren(...children) { this.children = children; },
      remove() { this.parent.children = this.parent.children.filter(child => child !== this); },
    };
    created.push(node);
    return node;
  };
  const body = element('body');
  const head = element('head');
  const window = { __deepLagoonWikiConfig: config };
  const context = {
    window,
    location: { pathname },
    document: { body, head, createElement: element,
      getElementById: id => [...body.children, ...head.children].find(node => node.id === id) },
    fetch: async (url, options) => {
      requests.push({ url, options });
      return { ok: true, json: async () => ({ data: { pages: { tree: pages } } }) };
    },
  };
  const run = () => vm.runInNewContext(script, context);
  run();
  await new Promise(setImmediate);
  return { created, requests, window, body, head, context, run };
}

test('configured locale and nested root drive GraphQL, filtering and every article URL', async () => {
  const result = await render({ root: '/en/guides/manual', locale: 'en' }, '/en/guides/manual/jobs', [
    { path: 'guides/manual', title: 'Home' },
    { path: 'guides/manual/jobs/engineer', title: 'Engineer' },
    { path: 'guides/manual_other', title: 'Wrong sibling' },
    { path: 'in_game/chemistry', title: 'Wrong root' },
  ]);
  assert.equal(result.requests.length, 1);
  assert.equal(result.requests[0].url, '/graphql');
  assert.equal(result.requests[0].options.credentials, 'omit');
  const payload = JSON.parse(result.requests[0].options.body);
  assert.deepEqual(payload.variables, { locale: 'en' });
  assert.match(payload.query, /locale: \$locale/);
  assert.deepEqual(result.created.filter((node) => node.tag === 'a').map((node) => node.href).sort(), [
    '/en/guides/manual', '/en/guides/manual/jobs/engineer',
  ]);
});

test('tree is absent outside configured root, with empty root, or with mismatched locale', async () => {
  for (const [config, path] of [
    [{ root: '/en/manual', locale: 'en' }, '/en/manual_other'],
    [{ root: '', locale: 'en' }, '/en/manual'],
    [{ root: '/ru/manual', locale: 'en' }, '/ru/manual'],
  ]) {
    const result = await render(config, path, []);
    assert.equal(result.requests.length, 0);
    assert.equal(result.created.some((node) => node.tag === 'aside'), false);
  }
});

test('default Russian tree remains supported', async () => {
  const result = await render({ root: '/ru/in_game', locale: 'ru' }, '/ru/in_game', [
    { path: 'in_game/chemistry', title: 'Chemistry' },
  ]);
  assert.equal(result.created.find((node) => node.tag === 'a').href, '/ru/in_game/chemistry');
});

test('repeated injection keeps one viewer and leaving the tree restores the page', async () => {
  const result = await render({ root: '/ru/in_game', locale: 'ru' }, '/ru/in_game', []);
  result.run();
  assert.equal(result.requests.length, 1);
  assert.equal(result.head.children.length, 1);
  assert.equal(result.body.children.length, 2);
  assert.ok(result.body.classList.values.has('dl-game-wiki'));
  assert.equal(result.body.style.marginLeft, undefined);
  const toggle = result.created.find(node => node.id === 'dl-game-wiki-toggle');
  toggle.onclick();
  assert.equal(toggle.attributes['aria-expanded'], 'true');
  assert.ok(result.body.classList.values.has('dl-game-wiki-tree-open'));
  toggle.onclick();
  assert.equal(toggle.attributes['aria-expanded'], 'false');
  result.context.location.pathname = '/ru/rules';
  result.run();
  assert.equal(result.head.children.length, 0);
  assert.equal(result.body.children.length, 0);
  assert.equal(result.body.classList.values.size, 0);
  assert.equal(result.window.__deepLagoonGuideTree, false);
  result.context.location.pathname = '/ru/in_game/jobs';
  result.run();
  assert.equal(result.requests.length, 2);
  assert.equal(result.head.children.length, 1);
});
