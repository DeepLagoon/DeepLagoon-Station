import assert from 'node:assert/strict';
import test from 'node:test';
import axios from 'axios';

const isolate = t => {
  const names = ['GITHUB_TOKEN', 'GITHUB_REPOSITORY', 'PR_NUMBER'];
  const environment = Object.fromEntries(names.map(name => [name, process.env[name]]));
  const adapter = axios.defaults.adapter;
  const authorization = axios.defaults.headers.common.Authorization;
  t.after(() => {
    for (const name of names) {
      if (environment[name] === undefined) delete process.env[name];
      else process.env[name] = environment[name];
    }
    axios.defaults.adapter = adapter;
    if (authorization === undefined) delete axios.defaults.headers.common.Authorization;
    else axios.defaults.headers.common.Authorization = authorization;
  });
  process.env.GITHUB_REPOSITORY = 'DeepLagoon/DeepLagoon-Station';
  process.env.PR_NUMBER = '13';
};

test('uses the workflow token and updates the intended PR title', async t => {
  isolate(t);
  process.env.GITHUB_TOKEN = 'test-token';
  const calls = [];
  axios.defaults.adapter = async config => {
    calls.push({ method: config.method, url: config.url, data: config.data });
    return { data: { title: 'улучшение редактора персонажа' }, status: 200,
      statusText: 'OK', headers: {}, config };
  };
  await import('./index.js?test=title');
  assert.deepEqual(calls.map(call => call.method), ['get', 'patch']);
  assert.ok(calls.every(call => call.url === 'https://api.github.com/repos/DeepLagoon/DeepLagoon-Station/pulls/13'));
  assert.equal(JSON.parse(calls[1].data).title, 'Улучшение Редактора Персонажа');
});

test('rejects a missing token before making any API request', async t => {
  isolate(t);
  delete process.env.GITHUB_TOKEN;
  let requests = 0;
  axios.defaults.adapter = async () => { requests++; throw new Error('Unexpected API request'); };
  await assert.rejects(import('./index.js?test=missing-token'), /GITHUB_TOKEN is required/);
  assert.equal(requests, 0);
});
