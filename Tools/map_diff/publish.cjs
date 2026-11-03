// Runs only from the default branch in workflow_run. Artifact data is never executed.
const fs = require('node:fs');
const path = require('node:path');
const MARKER = '<!-- station-map-diff:v1 -->';
const BRANCH = 'map-diff-previews';
const SHA = /^[a-f0-9]{40}$/;
const PREVIEW = /^preview-\d{3,}\.png$/;
const LIMIT = 8;

function loadReport(directory) {
  const file = path.join(directory, 'report.json');
  if (!fs.existsSync(file)) return null;
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.size > 16*1024*1024) throw new Error('Invalid map diff report');
  const report = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (report.version !== 1 || !Number.isSafeInteger(report.pr) || report.pr < 1 ||
      !SHA.test(report.head_sha) || !SHA.test(report.base_sha) || !Array.isArray(report.maps) || report.maps.length > 2000) {
    throw new Error('Invalid map diff report schema');
  }
  return report;
}

function previewBytes(directory, name) {
  if (!PREVIEW.test(name)) throw new Error('Invalid preview filename');
  const file = path.join(directory, name), stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.size > 10*1024*1024) throw new Error('Invalid preview size or file type');
  const bytes = fs.readFileSync(file);
  if (!bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10]))) throw new Error('Preview is not a PNG');
  return bytes;
}

const escape = value => String(value).slice(0,1000).replace(/[&<>\[\]`*_|!@\\]/g, ch => `&#${ch.charCodeAt(0)};`).replace(/[\r\n]/g,' ');

function body(report, urls, runUrl) {
  const lines = [MARKER, '### Визуальный diff карт Station', '',
    `Коммит: ${report.head_sha.slice(0,12)} · [Полный отчёт и изображения](${runUrl}) (artifact **station-map-diff**).`, '',
    'В изображениях: **до | после | изменения размещения**. Зелёный — добавлено, красный × — удалено, жёлтый — изменено или повёрнуто.', ''];
  let shown = 0, total = 0;
  for (const map of report.maps) {
    lines.push(`**${escape(map.path)}**`);
    if (map.error) lines.push('Не удалось отрисовать карту; подробности в отчёте Actions.');
    for (const grid of map.grids || []) {
      total++;
      const s = grid.stats || {};
      lines.push(`${escape(grid.name)}: тайлы +${Number(s.tiles_added)||0} / −${Number(s.tiles_removed)||0} / ~${Number(s.tiles_changed)||0}; объекты +${Number(s.objects_added)||0} / −${Number(s.objects_removed)||0}; повороты ${Number(s.objects_rotated)||0}; декали +${Number(s.decals_added)||0} / −${Number(s.decals_removed)||0}.`);
      if (urls[grid.preview]) {
        lines.push('', `![До, после и изменения карты](${urls[grid.preview]})`, '');
        shown++;
      }
      if (lines.join('\n').length > 45000) break;
    }
    if (map.warning) lines.push(escape(map.warning));
    lines.push('');
    if (lines.join('\n').length > 45000) { lines.push('Остальные карты — в полном отчёте Actions.'); break; }
  }
  if (!report.maps.length) lines.push('В текущей версии PR нет изменений YAML-карт.');
  if (total > shown) lines.push(`В комментарии ${shown} превью; полный набор доступен в artifact.`);
  lines.push('', 'Статистика сравнивает сохранённые тайлы, объекты на гридах, повороты и декали. Метаданные, содержимое контейнеров и настройки компонентов не входят в статистику. Обе версии отрисованы с прототипами текущего PR.');
  return lines.join('\n');
}

async function storeImages(github, repo, number, images) {
  const treeEntries = [];
  for (const [name, bytes] of images) {
    const blob = await github.rest.git.createBlob({...repo, content:bytes.toString('base64'), encoding:'base64'});
    treeEntries.push({path:name, mode:'100644', type:'blob', sha:blob.data.sha});
  }
  const subtree = await github.rest.git.createTree({...repo, tree:treeEntries});
  for (let attempt=0; attempt<5; attempt++) {
    let previous;
    try { previous = (await github.rest.git.getRef({...repo,ref:`heads/${BRANCH}`})).data.object.sha; }
    catch(e) { if(e.status!==404) throw e; }
    let baseTree;
    if (previous) baseTree = (await github.rest.git.getCommit({...repo,commit_sha:previous})).data.tree.sha;
    const tree = await github.rest.git.createTree({...repo, ...(baseTree?{base_tree:baseTree}:{}),
      tree:[{path:`pr-${number}`,mode:'040000',type:'tree',sha:subtree.data.sha}]});
    const commit = await github.rest.git.createCommit({...repo,message:`Map diff previews for PR #${number}`,
      tree:tree.data.sha,parents:previous?[previous]:[]});
    try {
      if (previous) await github.rest.git.updateRef({...repo,ref:`heads/${BRANCH}`,sha:commit.data.sha,force:false});
      else await github.rest.git.createRef({...repo,ref:`refs/heads/${BRANCH}`,sha:commit.data.sha});
      return commit.data.sha;
    } catch(e) {
      if (![409,422].includes(e.status) || attempt===4) throw e;
      // Another PR may have published concurrently. Rebase this subtree onto the current ref.
    }
  }
}

async function publish({github, context, core, artifactDir}) {
  const repo = context.repo, run = context.payload.workflow_run;
  if (run.event !== 'pull_request') return core.info('Not a PR render run');
  const report = loadReport(artifactDir);
  if (!report && ['success','skipped'].includes(run.conclusion)) return core.info('No render report: nothing to publish');
  // Resolve PR identity from GitHub, not from the uploaded artifact. Fork runs can have an empty pull_requests array.
  let linked = run.pull_requests || [];
  if (!linked.length) linked = await github.paginate(github.rest.repos.listPullRequestsAssociatedWithCommit, {...repo,commit_sha:run.head_sha,per_page:100});
  if (report) linked = linked.filter(pr=>pr.number===report.pr);
  if (linked.length !== 1) return core.info('No unambiguous PR associated with this run');
  const number = linked[0].number;
  const pr = (await github.rest.pulls.get({...repo,pull_number:number})).data;
  if (pr.state !== 'open' || pr.base.repo.id !== context.payload.repository.id) return;
  const runHead = linked[0].head?.sha || run.head_sha;
  if (pr.head.sha !== runHead || (report && (pr.head.sha!==report.head_sha || pr.base.sha!==report.base_sha))) {
    return core.info('Ignoring superseded map diff');
  }
  const comments = await github.paginate(github.rest.issues.listComments,{...repo,issue_number:number,per_page:100});
  const previous = comments.find(c=>c.user?.login==='github-actions[bot]' && c.body?.startsWith(MARKER));
  if (report && !report.maps.length && !previous) return;
  const urls = {};
  if (report) {
    const names = [...new Set(report.maps.flatMap(m=>(m.grids||[]).map(g=>g.preview)))].slice(0,LIMIT);
    const images = names.map(name=>[name,previewBytes(artifactDir,name)]);
    if (images.length) {
      const commit = await storeImages(github, repo, number, images);
      for (const [name] of images) urls[name] = `https://raw.githubusercontent.com/${repo.owner}/${repo.repo}/${commit}/pr-${number}/${name}`;
    }
  }
  // Do not overwrite a newer commit's comment if the PR changed during image publication.
  if ((await github.rest.pulls.get({...repo,pull_number:number})).data.head.sha !== pr.head.sha) return;
  const runUrl = `${context.serverUrl || 'https://github.com'}/${repo.owner}/${repo.repo}/actions/runs/${run.id}`;
  const text = report ? body(report,urls,runUrl) : `${MARKER}\n### Визуальный diff карт Station\n\nНе удалось построить отчёт для ${pr.head.sha.slice(0,12)}. [Лог рендера](${runUrl}).`;
  if (previous) await github.rest.issues.updateComment({...repo,comment_id:previous.id,body:text});
  else await github.rest.issues.createComment({...repo,issue_number:number,body:text});
}

module.exports = {publish, loadReport, previewBytes, body, storeImages, MARKER};
