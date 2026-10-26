const fs = require('node:fs');
const path = require('node:path');
const yaml = require('js-yaml');

const TYPES = { add: 'Add', remove: 'Remove', tweak: 'Tweak', fix: 'Fix' };

function parseChanges(body, fallbackAuthor) {
    const lines = (body || '').replace(/<!--[^]*?-->/g, '').split(/\r?\n/);
    const changes = [];
    let author = fallbackAuthor;
    let active = false;
    let found = false;
    for (const line of lines) {
        const header = /^\s*(?::cl:|🆑)\s*(.*?)\s*$/iu.exec(line);
        if (header) {
            if (!found) author = header[1] || fallbackAuthor;
            found = active = true;
            continue;
        }
        if (!active) continue;
        const entry = /^\s*(?:[-*]\s*)?(add|remove|tweak|fix):\s*(\S.*?)\s*$/iu.exec(line);
        if (entry) changes.push({ type: TYPES[entry[1].toLowerCase()], message: entry[2] });
        else if (line.trim()) active = false;
    }
    if (!found || changes.length === 0) return null;
    if (changes.length > 100 || changes.some(c => c.message.length > 1400) || author.length > 100)
        throw new Error('Changelog exceeds the shared delivery limits');
    return { author, changes };
}

function validPull(pr, repository) {
    return !!(pr.merged_at && pr.merged !== false && pr.base?.ref === 'main' &&
        pr.base?.repo?.full_name?.toLowerCase() === repository.toLowerCase());
}

function appendPulls(document, pulls, repository) {
    if (!document || typeof document !== 'object' || !Array.isArray(document.Entries))
        throw new Error('Invalid changelog document');
    const known = new Set(document.Entries.map(entry => entry.url));
    let next = document.Entries.reduce((max, entry) => Math.max(max, Number.isSafeInteger(entry.id) ? entry.id : 0), 0);
    let count = 0;
    for (const pr of [...pulls].sort((a, b) => new Date(a.merged_at) - new Date(b.merged_at) || a.number - b.number)) {
        if (!validPull(pr, repository)) continue;
        const url = `https://github.com/${repository}/pull/${pr.number}`;
        const parsed = parseChanges(pr.body, pr.user?.login || 'unknown');
        if (parsed && !known.has(url)) {
            document.Entries.push({ ...parsed, id: ++next, time: new Date(pr.merged_at).toISOString(), url });
            known.add(url);
            count++;
        }
        if (!document.GeneratedThrough || new Date(document.GeneratedThrough) < new Date(pr.merged_at))
            document.GeneratedThrough = new Date(pr.merged_at).toISOString();
    }
    return count;
}

async function collectPulls(getPage, since, maximumPages = 100) {
    const result = [];
    for (let page = 1; page <= maximumPages; page++) {
        const rows = await getPage(page);
        if (!Array.isArray(rows)) throw new Error('Invalid PR page');
        result.push(...rows.filter(pr => pr.merged_at && new Date(pr.merged_at) >= since));
        if (rows.length < 100 || new Date(rows[rows.length - 1].updated_at) < since) return result;
    }
    throw new Error('PR backlog page limit reached; watermark was not advanced');
}

async function main() {
    const repository = process.env.GITHUB_REPOSITORY;
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository || '')) throw new Error('Invalid repository');
    const axios = require('axios');
    const api = axios.create({ baseURL: `https://api.github.com/repos/${repository}/`, timeout: 15000,
        maxRedirects: 0, maxContentLength: 10_000_000,
        headers: { Accept: 'application/vnd.github+json', ...(process.env.GITHUB_TOKEN ? { Authorization: `Bearer ${process.env.GITHUB_TOKEN}` } : {}) } });
    const file = path.resolve(__dirname, '../..', process.env.CHANGELOG_DIR || 'Resources/Changelog/Monolith.yml');
    const original = fs.readFileSync(file, 'utf8');
    const document = yaml.load(original);
    const current = process.env.PR_NUMBER ? (await api.get(`pulls/${process.env.PR_NUMBER}`)).data : null;
    if (current && !validPull(current, repository)) throw new Error('PR is not merged into this repository main');
    // Overlap the durable watermark to recover replaced workflow runs and API visibility delays.
    const anchor = process.env.CHANGELOG_SINCE || document.GeneratedThrough || current?.merged_at || new Date().toISOString();
    const since = new Date(new Date(anchor).getTime() - 86400000);
    if (Number.isNaN(since.getTime())) throw new Error('Invalid backlog date');
    const pulls = await collectPulls(async page => (await api.get('pulls', { params: {
        state: 'closed', base: 'main', sort: 'updated', direction: 'desc', per_page: 100, page } })).data, since);
    if (current) pulls.push(current);
    const before = JSON.stringify(document);
    const added = appendPulls(document, pulls, repository);
    if (before !== JSON.stringify(document)) {
        const temporary = file + '.tmp';
        fs.writeFileSync(temporary, yaml.dump(document, { noRefs: true, lineWidth: 100, sortKeys: false }));
        fs.renameSync(temporary, file);
    }
    console.log(`Changelog: ${added} new PR entries; existing history retained`);
}

module.exports = { parseChanges, appendPulls, collectPulls, validPull };
if (require.main === module) main().catch(error => { console.error(`Changelog generation failed: ${error.name}`); process.exitCode = 1; });
