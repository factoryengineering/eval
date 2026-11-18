import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { apiBuckets, apiFailures, mockApiAnswer, sourcesOf } from '../src/api.js';
import type { ApiCheck, ApiResult } from '../src/api.js';
import { CATALOG } from '../src/api-catalog.js';
import { bucketOf, checksOf, report, runBaseline, summarize } from '../src/baseline.js';
import { loadCases } from '../src/cases.js';
import type { Config, EvalCase, GradeNotes } from '../src/types.js';

const check = (layer: string, id: string, passed: boolean, group: string | null = null, advisory = false): ApiCheck => ({ layer, id, passed, evidence: `evidence of ${id}`, group, advisory });
const result = (checks: ApiCheck[], ok = true): ApiResult => ({ id: '0', compile: { ok, errors: ok ? [] : ['CS0103 nope'] }, checks });
const spec = (graded?: string[]): EvalCase => ({ id: 'c', prompt: '', targets: [], must: [], api: { domain: 'blog', level: 1, graded } });

const everything = result([
  check('scenario', 'create-succeeds', false, 'create'),
  check('scenario', 'replace-with-current-if-match-updates', false, 'replace'),
  check('scenario', 'read-unknown-id-is-not-found', false, 'read'),
  check('static', 'etag-header-set', false),
  check('lint', 'delete-no-404', false),
  check('spec', 'spec-declares-observed-statuses', false),
  check('static', 'no-check-then-insert', false, null, true),
  check('static', 'routes-use-guid', true),
]);

test('without graded every failing check counts, with its bucket and never the advisory', () => {
  const failures = apiFailures(spec(), everything);
  assert.deepEqual(failures.map(bucketOf), ['api scenario create', 'api scenario replace', 'api scenario read', 'api static etag-header-set', 'api lint delete-no-404', 'api spec spec-declares-observed-statuses']);
  assert.match(failures[0]!, /^api scenario create: create-succeeds: evidence of create-succeeds$/);
});

test('graded accepts layer prefixes, layer:id, layer:group and layer:scenario-name', () => {
  assert.deepEqual(apiFailures(spec(['lint', 'spec']), everything).map(bucketOf), ['api lint delete-no-404', 'api spec spec-declares-observed-statuses']);
  assert.deepEqual(apiFailures(spec(['scenario:replace', 'static:etag-header-set']), everything).map(bucketOf), ['api scenario replace', 'api static etag-header-set']);
  assert.deepEqual(apiFailures(spec(['scenario:read-unknown-id-is-not-found']), everything).map(bucketOf), ['api scenario read']);
  assert.deepEqual(apiFailures(spec(['static:routes-use-guid']), everything), []);
});

test('compile errors and host failures are never hidden by graded; an unreadable response is an error', () => {
  assert.deepEqual(apiFailures(spec(['lint']), result([], false)), ['api compile: CS0103 nope']);
  assert.deepEqual(apiFailures(spec(['lint']), result([check('host', 'host-starts', false)])), ['api host: evidence of host-starts']);
  assert.deepEqual(apiFailures(spec(), { ...result([]), error: 'boom' }), ['api error: boom']);
});

test('advisories are recorded as notes, never as failures; a failed create is recorded as a bootstrap note even when not graded', () => {
  const notes: GradeNotes = { notes: [] };
  const failures = apiFailures(spec(['lint']), everything, notes);
  assert.equal(failures.length, 1);
  assert.deepEqual(notes.notes, ['api advisory no-check-then-insert: evidence of no-check-then-insert']);
  assert.match(notes.bootstrapNote!, /create-succeeds failed.*2 later scenario\(s\) failed/);
  assert.equal(bucketOf(notes.notes[0]!), 'api advisory');
  const clean: GradeNotes = { notes: [] };
  apiFailures(spec(), result([check('scenario', 'create-succeeds', true, 'create')]), clean);
  assert.equal(clean.bootstrapNote, undefined);
});

test('sourcesOf uses every fenced block, or the whole text when there is none', () => {
  assert.deepEqual(sourcesOf('a\n```csharp\nX\n```\ntext\n```cs\nY\n```'), ['X\n', 'Y\n']);
  assert.deepEqual(sourcesOf('plain'), ['plain']);
});

test('checksOf lists the api buckets of the graded checks and no ef-linq buckets for api-only cases', () => {
  const checks = checksOf([spec(['scenario:create-succeeds', 'static:etag-header-set', 'lint'])]);
  assert.ok(checks.includes('api scenario create') && checks.includes('api static etag-header-set') && checks.includes('api lint delete-no-404'));
  assert.ok(!checks.includes('api spec spec-declares-observed-statuses') && !checks.includes('test failed') && checks.includes('api compile'));
  assert.ok(checksOf([spec(), { id: 'x', prompt: '', targets: [], must: [] }]).includes('test failed'));
  assert.equal(apiBuckets(undefined).length, 2 + new Set(Object.values(CATALOG.scenarios)).size + CATALOG.static.length + CATALOG.lint.length + CATALOG.spec.length);
});

test('baseline reports cascades: first fail, independent failures and advisories', async () => {
  const config: Config = { skill: '', ids: '', cases: '', trials: 2, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };
  const outcomes = [
    { fails: ['api scenario create: create-succeeds: x', 'api scenario replace: r: x'], notes: { notes: ['api advisory a: x'], bootstrapNote: 'masked' } },
    { fails: ['api static etag-header-set: x'], notes: { notes: [] } },
  ];
  let n = 0;
  const complete = async () => ({ text: '', inputTokens: 1, outputTokens: 1, estimated: true });
  const grade = async (_c: EvalCase, _t: string, notes?: GradeNotes) => {
    const o = outcomes[n++]!;
    Object.assign(notes!, o.notes);
    return o.fails;
  };
  const trials = await runBaseline(config, [spec()], grade, complete, undefined, 1);
  assert.equal(trials[0]!.bootstrapNote, 'masked');
  assert.equal(trials[1]!.bootstrapNote, undefined);
  const [cell] = summarize(trials);
  assert.equal(cell!.cascade!.bootstrapFailed, 0.5);
  assert.deepEqual(cell!.cascade!.firstFail, { 'api scenario create': 0.5, 'api static etag-header-set': 0.5 });
  assert.deepEqual(cell!.cascade!.independent, { 'api static etag-header-set': 1 });
  assert.equal(cell!.cascade!.independentN, 1);
  assert.deepEqual(cell!.cascade!.advisories, { 'api advisory': 0.5 });
  assert.match(report([cell!]), /create bootstrap failed 0\.50.*\n.*independent of the bootstrap \(n=1\): api static etag-header-set 1\.00\n.*advisory \(not failing\): api advisory 0\.50/);
});

test('mock answers: naive blog code below level 3, the reference subject at level 3, a marker without a naive implementation', async () => {
  const cases = await loadCases('idempotent-writes/evals/cases.json');
  const prompt = (id: string) => cases.find((c) => c.id === id)!.prompt;
  assert.match(await mockApiAnswer(prompt('blog-naked')), /Guid\.NewGuid\(\)/);
  assert.match(await mockApiAnswer(prompt('blog-specified')), /ProblemExceptionFilter|ProblemDetails/);
  assert.match(await mockApiAnswer(prompt('support-mild')), /no naive implementation/);
  assert.match(await mockApiAnswer(prompt('logistics-specified')), /Consignment/);
});

const GROUPS = ['create', 'read', 'replace', 'delete', 'server-owned', 'race', 'lost-response', 'canonical'];

test('generated cases: 3 domains x 3 levels per home, same prompts, ids and graded as designed', async () => {
  const [idem, design] = await Promise.all([loadCases('idempotent-writes/evals/cases.json'), loadCases('api-design/evals/cases.json')]);
  for (const cases of [idem, design]) {
    assert.equal(cases.length, 9);
    assert.deepEqual(cases.map((c) => c.id), ['blog', 'support', 'logistics'].flatMap((d) => ['naked', 'mild', 'specified'].map((l) => `${d}-${l}`)));
    assert.ok(cases.every((c) => c.api && c.targets.length === 0 && c.prompt.startsWith('Implement an HTTP API with ASP.NET Core controllers')));
  }
  assert.deepEqual(idem.map((c) => c.prompt), design.map((c) => c.prompt));
  const forbidden = /idempoten|ETag|If-Match|retr|concurren|tombstone|409|412|428/i;
  for (const c of idem.filter((x) => x.api!.level === 1)) assert.doesNotMatch(c.prompt, forbidden, `${c.id} must not hint at the opinions`);
  for (const c of idem.filter((x) => x.api!.level === 2)) {
    assert.match(c.prompt, /Clients may retry requests after timeouts and several clients may edit the same item concurrently\./);
    assert.doesNotMatch(c.prompt.replace(/Clients may retry[^\n]*/, ''), forbidden);
  }
  for (const c of idem.filter((x) => x.api!.level === 3)) assert.match(c.prompt, /If-Match.*428|428.*If-Match/s);
  assert.ok(GROUPS.length === 8);
});

test('check-to-home mapping: every check is graded in a home; the only overlaps are the status-code scenarios and the compile gate', async () => {
  const graded = (home: string) => (JSON.parse(readFileSync(`${home}/evals/cases.json`, 'utf8')) as EvalCase[])[0]!.api!.graded!;
  const owners = (home: string) => new Set(apiBuckets(graded(home)).filter((b) => b !== 'api compile' && b !== 'api host'));
  const names = (home: string) => new Set(Object.entries(CATALOG.scenarios).filter(([id, group]) => graded(home).some((g) => g === `scenario:${group}` || g === `scenario:${id}`)).map(([id]) => id));
  const idem = owners('idempotent-writes');
  const design = owners('api-design');
  const all = new Set([...apiBuckets(undefined)].filter((b) => b !== 'api compile' && b !== 'api host'));
  const missing = [...all].filter((b) => !idem.has(b) && !design.has(b));
  assert.deepEqual(missing, ['api static no-check-then-insert']);
  const overlap = [...names('idempotent-writes')].filter((n) => names('api-design').has(n)).sort();
  assert.equal(overlap.length, 10);
  assert.ok(overlap.every((n) => /precondition|conflict/.test(n)), overlap.join(', '));
  for (const layer of ['static', 'lint', 'spec']) assert.deepEqual([...idem].filter((b) => b.startsWith(`api ${layer} `) && design.has(b)), []);
});

const FROZEN = {
  'blog-naked': 'f58b018c4d4c641b', 'blog-mild': 'eb18de256e97f870', 'support-naked': 'a64558fc75736a1c', 'support-mild': '69b547aab1b5437f',
  'logistics-naked': 'fe037858f4ad6882', 'logistics-mild': 'ed4cec0c36089cb7',
} as const;

test('iteration A11: level 1 and 2 prompts are byte-identical to the baseline; level 3 states identical racing PUTs and the ETag on a no-op PUT', async () => {
  for (const home of ['idempotent-writes', 'api-design']) {
    const cases = await loadCases(`${home}/evals/cases.json`);
    for (const [id, hash] of Object.entries(FROZEN)) assert.equal(createHash('sha256').update(cases.find((c) => c.id === id)!.prompt).digest('hex').slice(0, 16), hash, `${home} ${id} changed`);
    for (const c of cases.filter((x) => x.id.endsWith('-specified'))) {
      assert.match(c.prompt, /If the two PUTs are identical, both succeed with 204 and the same ETag: the second is treated as already applied, never 412/);
      assert.match(c.prompt, /a PUT that changes nothing is 204 and carries the unchanged ETag/);
    }
  }
});
