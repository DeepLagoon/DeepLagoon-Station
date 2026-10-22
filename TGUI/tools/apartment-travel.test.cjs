const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { transformSync } = require('esbuild');

// Render the real JSX with server-provided snapshots; no game/client startup.
const source = fs.readFileSync(path.resolve(__dirname, '../packages/tgui/interfaces/ApartmentTravel.jsx'), 'utf8')
  .replace(/^import .*;\r?$/gm, '')
  .replace(/export const /g, 'const ');
const code = transformSync(source + '\nglobalThis.Panel = ApartmentTravel;', { loader: 'jsx' }).code;
function render(data) {
  const context = vm.createContext({ React, useState: React.useState, useBackend: () => ({ data, act() {} }) });
  vm.runInContext(code, context);
  return renderToStaticMarkup(React.createElement(context.Panel));
}

test('terminal directs travel through the cabin and exposes editing only to the owner', () => {
  const guest = render({ atHome: true, canManage: false });
  const owner = render({ atHome: true, canManage: true });
  assert.match(guest, /пультом лифта/);
  assert.doesNotMatch(guest, /Обустройство квартиры|Пригласить|Принять и отправиться/);
  assert.match(owner, /Обустройство квартиры/);
});

function renderElevator(data) {
  const source = fs.readFileSync(path.resolve(__dirname, '../packages/tgui/interfaces/ApartmentElevator.jsx'), 'utf8')
    .replace(/^import .*;\r?$/gm, '').replace(/export const /g, 'const ');
  const code = transformSync(source + '\nglobalThis.Panel = ApartmentElevator;', { loader: 'jsx' }).code;
  const context = vm.createContext({ React, useState: React.useState, useBackend: () => ({ data, act() {} }) });
  vm.runInContext(code, context);
  return renderToStaticMarkup(React.createElement(context.Panel));
}
test('elevator shows the personal number and only offers return inside an apartment', () => {
  const outward = renderElevator({ number: 142, atHome: false, busy: false });
  assert.match(outward, /Ваш номер/);
  assert.match(outward, /142/);
  assert.match(outward, /Номер квартиры/);
  assert.doesNotMatch(outward, /Пригласить|Принять и отправиться/);
  const inward = renderElevator({ number: 142, atHome: true, busy: true });
  assert.match(inward, /Вернуться на жилую остановку/);
  assert.match(inward, /disabled/);
  assert.doesNotMatch(inward, /id="apartment-number"/);
});
