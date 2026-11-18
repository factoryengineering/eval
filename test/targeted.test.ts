import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { mockApiAnswer, sourcesOf } from '../src/api.js';
import { loadCases } from '../src/cases.js';

interface Line { id: string; text: string; owns: string[]; monitors?: string[]; needs: string[]; withheld: string; mutants: Record<string, string[]> }
const HOMES = ['idempotent-writes', 'api-design'] as const;
const DOMAINS = ['blog', 'support', 'logistics'];
const lines = (home: string) => JSON.parse(readFileSync(`${home}/evals/lines.json`, 'utf8')) as Line[];
const BLOCKS = ['retry', 'create-id', 'create-status', 'create-replay', 'read-basic', 'read-etag', 'etag-format', 'etag-spellings', 'etag-server-owned', 'etag-loose', 'ifmatch-etag-generic',
  'conditional', 'put-update', 'put-428', 'mech-428', 'put-already', 'put-race', 'put-race-identical', 'delete-core', 'delete-428', 'delete-idem', 'errors'];
const sha = (s: string) => createHash('sha256').update(s).digest('hex').slice(0, 16);

test('lines: ids are unique per home, owned checks are not shared between lines of a home, every line has a mutant per domain and known prerequisite blocks', () => {
  for (const home of HOMES) {
    const ls = lines(home);
    assert.equal(new Set(ls.map((l) => l.id)).size, ls.length);
    const owned = ls.flatMap((l) => [...l.owns, ...(l.monitors ?? [])]);
    assert.equal(new Set(owned).size, owned.length, `${home}: a check is owned by two lines`);
    for (const l of ls) {
      assert.ok(l.text.length > 20 && l.owns.length > 0, l.id);
      assert.deepEqual(Object.keys(l.mutants).sort(), [...DOMAINS].sort(), l.id);
      assert.ok(Object.values(l.mutants).every((m) => m.length >= 1), l.id);
      assert.ok(l.needs.every((b) => BLOCKS.includes(b)), `${l.id}: unknown prerequisite block`);
    }
  }
});

test('targeted cases: 3 domains per line, targets the line, grades only its checks, base prompt plus prerequisites only, withheld opinion never stated', async () => {
  for (const home of HOMES) {
    const cases = await loadCases(`${home}/evals/cases.json`);
    const base = new Map(cases.filter((c) => c.targets.length === 0).map((c) => [c.id, c.prompt]));
    for (const l of lines(home)) {
      for (const domain of DOMAINS) {
        const c = cases.find((x) => x.id === `${l.id}-${domain}`)!;
        assert.ok(c, `${home}: case ${l.id}-${domain}`);
        assert.deepEqual(c.targets, [l.id]);
        assert.equal(c.guard, undefined);
        assert.deepEqual(c.api!.graded, [...l.owns, ...(l.monitors ?? [])]);
        assert.equal(c.api!.domain, domain);
        const naked = base.get(`${domain}-naked`)!;
        assert.ok(c.prompt.startsWith(naked), `${c.id}: starts with the naked prompt`);
        assert.doesNotMatch(c.prompt, new RegExp(l.withheld, 'i'), `${c.id}: leaks the opinion under test`);
        if (l.needs.length === 0) assert.equal(c.prompt, naked, `${c.id}: no prerequisites means the naked prompt`);
        const extra = c.prompt.slice(naked.length);
        assert.equal(/Clients may retry/.test(extra), l.needs.includes('retry'), `${c.id}: retry sentence follows the dependency table`);
        assert.equal((extra.match(/\n- /g) ?? []).length, l.needs.filter((b) => b !== 'retry').length, `${c.id}: one convention per prerequisite block`);
      }
    }
    assert.equal(cases.filter((c) => c.targets.length > 0).length, lines(home).length * 3);
  }
});

test('guards: the three level 3 cases of each home are the guards and grade everything their baseline case grades', async () => {
  for (const home of HOMES) {
    const cases = await loadCases(`${home}/evals/cases.json`);
    assert.deepEqual(cases.filter((c) => c.guard).map((c) => c.id), DOMAINS.map((d) => `${d}-specified`));
    for (const c of cases.filter((x) => x.guard)) assert.equal(c.targets.length, 0);
    for (const c of cases.filter((x) => x.id.endsWith('-naked') || x.id.endsWith('-mild'))) assert.equal(c.guard, undefined);
  }
});

test('the level 3 prompts are pinned after iteration A11 (changing them again is a spec change to record)', async () => {
  const cases = await loadCases('idempotent-writes/evals/cases.json');
  const pinned: Record<string, string> = { 'blog-specified': 'e35df8b5fc501d8c', 'support-specified': '075a7cd019971c15', 'logistics-specified': '2a228836888e49b2' };
  for (const [id, hash] of Object.entries(pinned)) assert.equal(sha(cases.find((c) => c.id === id)!.prompt), hash, id);
});

test('mock answers for targeted prompts are the first bad reference of the case; ambiguous prompts fall back to the generic mock', async () => {
  for (const home of HOMES) {
    const cases = await loadCases(`${home}/evals/cases.json`);
    const refs = JSON.parse(readFileSync(`${home}/evals/references.json`, 'utf8')) as Record<string, { bad: Record<string, string> }>;
    const naked = new Set(cases.filter((c) => c.targets.length === 0).map((c) => c.prompt));
    for (const c of cases.filter((x) => x.targets.length > 0)) {
      const answer = await mockApiAnswer(c.prompt);
      if (naked.has(c.prompt)) assert.ok(!Object.values(refs[c.id]!.bad).includes(answer), `${c.id}: ambiguous prompt keeps the generic mock`);
      else assert.deepEqual(sourcesOf(answer), sourcesOf(Object.values(refs[c.id]!.bad)[0]!), c.id);
    }
  }
});

const BLOG_WORDS = /\b(blogs?|[Pp]osts?|tickets?|shipments?|warehouses?|depots?|consignments?|views?|downloads?|titles?|authors?|comments?)\b/;

test('skills: lines are stored exactly in skill-lines.json under the ids of the targeted cases, are domain-neutral, and every skill line has a signal or is flagged provisional', async () => {
  for (const home of HOMES) {
    const skill = readFileSync(`.devin/skills/${home}/SKILL.md`, 'utf8');
    const ids = JSON.parse(readFileSync(`${home}/evals/skill-lines.json`, 'utf8')) as Record<string, string>;
    const ls = lines(home) as (Line & { provisional?: boolean })[];
    assert.deepEqual(Object.keys(ids), ls.map((l) => l.id), `${home}: one skill line per candidate line, in table order`);
    const bullets = skill.split('\n').filter((l) => l.startsWith('- ')).map((l) => l.slice(2));
    assert.deepEqual(bullets, Object.values(ids), `${home}: SKILL.md lines equal skill-lines.json`);
    assert.match(skill, new RegExp(`^---\\nname: ${home}\\ndescription: [^:\\n]+\\n---\\nRules for `));
    for (const [id, text] of Object.entries(ids)) {
      assert.doesNotMatch(text, BLOG_WORDS, `${id} contains a domain word`);
      assert.ok(text.length < 600 && !text.includes('\n'), id);
    }
    assert.doesNotMatch(skill, BLOG_WORDS, `${home}: SKILL.md contains a domain word`);
    for (const l of ls) assert.ok(l.provisional === undefined || l.provisional === true, l.id);
  }
});
