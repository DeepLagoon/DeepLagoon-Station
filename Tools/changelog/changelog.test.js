const test = require('node:test');
const assert = require('node:assert/strict');
const yaml = require('js-yaml');
const { parseChanges, appendPulls, collectPulls } = require('./changelog');
const repository = 'DeepLagoon/DeepLagoon-Station';
const pull = (number, body = ':cl:\n- add: Good change', time = '2026-10-26T10:00:00Z') => ({
    number, body, merged_at: time, updated_at: time, merged: true, user: { login: 'tester' },
    base: { ref: 'main', repo: { full_name: repository } },
});

test('header author never consumes the next line; Cyrillic authors and all types work', () => {
    assert.equal(parseChanges(':cl:\n- add: First', 'tester').author, 'tester');
    const parsed = parseChanges('🆑 Автор теста\n- ADD: A\n* fix: B\nremove: C\ntweak: D', 'fallback');
    assert.equal(parsed.author, 'Автор теста');
    assert.deepEqual(parsed.changes.map(c => c.type), ['Add', 'Fix', 'Remove', 'Tweak']);
});
test('ignores commented template, null and empty entries, and lines outside the block', () => {
    assert.equal(parseChanges(null, 'tester'), null);
    assert.equal(parseChanges('<!-- :cl:\n- add: Example -->', 'tester'), null);
    assert.equal(parseChanges(':cl:\n\n## Notes\nfix: outside', 'tester'), null);
    assert.equal(parseChanges('fix: before\n:cl:\nfix: within\n## Notes\nadd: after', 'tester').changes.length, 1);
});
test('stable PR identity preserves metadata and history across repeated runs', () => {
    const document = { Name: 'Monolith', Order: -2, AdminOnly: false, Extra: { retained: true }, Entries: [{ id: 42, url: 'old' }] };
    assert.equal(appendPulls(document, [pull(1), pull(1)], repository), 1);
    assert.equal(appendPulls(document, [pull(1)], repository), 0);
    assert.equal(document.Entries.length, 2);
    assert.equal(document.Entries[1].id, 43);
    assert.equal(document.Extra.retained, true);
    assert.equal(yaml.load(yaml.dump(document)).Name, 'Monolith');
});
test('only merged PRs into the same main feed are appended', () => {
    const other = pull(2); other.base.ref = 'testing';
    const fork = pull(3); fork.base.repo.full_name = 'other/repo';
    const open = pull(4); open.merged_at = null;
    const document = { Entries: [] };
    assert.equal(appendPulls(document, [other, fork, open], repository), 0);
});
test('IDs follow merge order, empty changelogs do not create entries', () => {
    const document = { Entries: [] };
    appendPulls(document, [pull(2), pull(1, ':cl:\nfix: Earlier', '2026-10-25T00:00:00Z'), pull(3, ':cl:')], repository);
    assert.deepEqual(document.Entries.map(e => e.url), [`https://github.com/${repository}/pull/1`, `https://github.com/${repository}/pull/2`]);
});
test('backlog covers more than one replaced workflow run', async () => {
    const first = Array.from({ length: 100 }, (_, i) => pull(i + 1));
    const pages = [first, [pull(101)]];
    const rows = await collectPulls(async n => pages[n - 1], new Date('2026-10-25'));
    assert.equal(rows.length, 101);
});
test('backlog cap fails without silently advancing or pruning', async () => {
    await assert.rejects(collectPulls(async () => Array.from({ length: 100 }, (_, i) => pull(i + 1)), new Date('2026-10-25'), 2));
});
test('delivery limits are enforced before generating an undeliverable entry', () => {
    assert.throws(() => parseChanges(':cl:\nadd: ' + 'x'.repeat(1401), 'tester'));
    assert.throws(() => parseChanges(':cl:\n' + 'add: A\n'.repeat(101), 'tester'));
});
