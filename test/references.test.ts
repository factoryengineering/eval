import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { ApiRunner, apiFailures, sourcesOf } from '../src/api.js';
import type { ApiResult } from '../src/api.js';
import { loadCases } from '../src/cases.js';
import { ExecRunner, makeGrader } from '../src/exec.js';

const dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll';
const fence = (code: string) => '```csharp\n' + code + '\n```';
const reasons: Record<string, string> = {
  'failing-assertion': 'test failed',
  'helper-async-suffix': 'rule gherkin-helpers',
  'helper-not-gherkin': 'rule gherkin-helpers',
  'plain-test-names': 'rule given-when-then',
  'weak-single-post': 'mutant survived',
  'connection-in-test': 'rule no-connection-in-tests',
  'discard-usings': 'rule no-discard-usings',
  'orphan-posts': 'test failed',
};
const suites = [
  { name: 'ef-linq', cases: 'evals/cases.json', references: 'evals/references.json' },
  { name: 'unit-testing', cases: 'unit-testing/evals/cases.json', references: 'unit-testing/evals/references.json' },
];

for (const suite of suites) {
  const refs = JSON.parse(readFileSync(suite.references, 'utf8')) as Record<string, { good: string; bad: Record<string, string> }>;

  test(`${suite.name}: every case has a reference solution`, async () => {
    const cases = await loadCases(suite.cases);
    assert.deepEqual(Object.keys(refs).sort(), cases.map((c) => c.id).sort());
  });

  test(`${suite.name}: graders accept the good reference and reject every bad one`, { skip: !existsSync(dll) && 'build with: npm run build:runner' }, async () => {
    const cases = await loadCases(suite.cases);
    const runner = new ExecRunner(dll);
    try {
      const grade = makeGrader(runner, { bad: async (ids) => ids.filter((id) => id.length < 2) });
      for (const c of cases) {
        const ref = refs[c.id]!;
        assert.deepEqual(await grade(c, fence(ref.good)), [], `${c.id}: good reference should pass`);
        for (const [name, code] of Object.entries(ref.bad)) {
          const failures = await grade(c, fence(code));
          assert.notDeepEqual(failures, [], `${c.id}: bad reference "${name}" should fail`);
          const reason = suite.name === 'unit-testing' ? reasons[name] : undefined;
          if (reason) assert.ok(failures.every((f) => f.startsWith(reason)), `${c.id}: "${name}" should fail only for "${reason}", got ${failures.join(' | ')}`);
        }
      }
    } finally {
      runner.close();
    }
  });
}

const apiDll = 'dotnet/ApiConformance.Cli/bin/Release/net10.0/ApiConformance.Cli.dll';
const exact = (...ids: string[]) => ({ ids, exact: true });
const has = (...ids: string[]) => ({ ids, exact: false });
const replayFamily = has('create-replay-returns-same-success', 'lost-response-create-retry-succeeds');
const canonicalOrder = exact('canonical-property-order-does-not-change-etag');
const deleteRace500 = exact('race-delete-same-if-match-both-succeed', 'race-put-then-delete-same-etag-is-serializable');
const specStatus = (rule: string) => exact(rule, 'spec-declares-observed-statuses');

const apiReasons: Record<string, Record<string, { ids: string[]; exact: boolean }>> = {
  'idempotent-writes': {
    'server-generates-id': has('create-succeeds', 'create-replay-returns-same-success'),
    'replay-409': replayFamily,
    'logistics-replay-409': replayFamily,
    'put-stale-same-body-412': exact('replace-retry-with-stale-if-match-same-body-succeeds', 'replace-identical-body-with-stale-if-match-succeeds', 'race-put-same-body-same-etag-both-succeed', 'lost-response-put-retry-succeeds', 'duplicate-delivery-put-succeeds-twice'),
    'delete-404-on-repeat': exact('delete-retry-is-no-content', 'race-delete-same-if-match-both-succeed', 'lost-response-delete-retry-succeeds', 'duplicate-delivery-delete-succeeds-twice'),
    'check-then-insert': exact('race-create-same-id-same-body-both-succeed', 'race-create-same-id-different-body-one-conflicts', 'race-child-create-same-id-different-parent-one-conflicts'),
    'put-no-retry-on-concurrency-exception': exact('race-put-same-body-same-etag-both-succeed', 'race-delete-then-put-same-etag-is-serializable'),
    'delete-concurrency-500': deleteRace500,
    'logistics-delete-concurrency-500': deleteRace500,
    'etag-property-order-sensitive': canonicalOrder,
    'support-etag-property-order-sensitive': canonicalOrder,
    'hash-includes-server-owned': has('server-owned-change-does-not-change-etag', 'replace-with-current-if-match-updates'),
    'tombstone-hard-deletes': has('delete-with-current-if-match-tombstones', 'replace-tombstoned-is-gone'),
    'no-unique-index': has('public-id-unique-index', 'create-same-id-different-body-conflicts'),
    'no-concurrency-token': exact('etag-concurrency-token', 'race-put-different-body-same-etag-one-precondition-failed', 'race-put-then-delete-same-etag-is-serializable'),
    'rowversion-etag': exact('no-rowversion-etag'),
    'no-tombstone-filter': exact('tombstone-property'),
    'creation-hash-mutable': exact('creation-hash-immutable'),
    'etag-column-nullable': exact('etag-column-not-null'),
    'support-server-owned-concurrency-token': exact('server-owned-change-does-not-invalidate-if-match', 'server-owned-not-concurrency-token'),
  },
  'api-design': {
    'put-without-if-match-allowed': exact('replace-without-if-match-is-precondition-required', 'replace-without-if-match-on-unknown-id-is-precondition-required', 'write-ops-require-if-match'),
    'delete-without-if-match-allowed': exact('delete-without-if-match-is-precondition-required', 'write-ops-require-if-match'),
    'put-ignores-if-match': exact('replace-stale-if-match-different-body-is-precondition-failed', 'race-put-different-body-same-etag-one-precondition-failed'),
    'no-parent-check-on-replay': exact('child-create-same-id-different-parent-conflicts'),
    'put-allows-parent-change': exact('child-replace-different-parent-conflicts'),
    'get-ignores-if-none-match': exact('read-matching-if-none-match-is-not-modified'),
    'entity-returned': has('no-entity-in-responses', 'read-returns-representation-and-etag'),
    'int-id-in-route': exact('routes-use-guid', 'internal-key-int-hidden'),
    'int-id-in-dto': exact('no-int-id-in-dtos', 'internal-key-int-hidden', 'ids-format-uuid'),
    'etag-header-missing': exact('etag-header-set', 'replace-stale-if-match-different-body-is-precondition-failed'),
    'if-match-optional': exact('if-match-required', 'write-ops-require-if-match', 'replace-without-if-match-is-precondition-required', 'replace-without-if-match-on-unknown-id-is-precondition-required', 'replace-stale-if-match-different-body-is-precondition-failed', 'race-put-different-body-same-etag-one-precondition-failed'),
    'no-cancellation-token': exact('cancellation-token-passed'),
    'problem-details-absent': exact('problem-details-used'),
    'md5-etag': has('sha256-used-for-etag', 'read-returns-representation-and-etag'),
    'openapi-delete-no-428': specStatus('write-ops-declare-428'),
    'openapi-put-no-410': specStatus('put-and-delete-declare-no-conflicting-gaps'),
    'openapi-put-no-404': specStatus('put-declares-412-and-404'),
    'openapi-delete-declares-404': exact('delete-no-404'),
    'openapi-create-no-200': specStatus('create-declares-201-200-409'),
    'openapi-get-no-304': specStatus('read-declares-304-404-410'),
    'openapi-error-not-problem-json': exact('error-responses-problem-json', 'spec-response-bodies-match-schemas'),
    'openapi-int-id-query-param': exact('no-int-id-params', 'routes-use-guid', 'internal-key-int-hidden'),
    'openapi-server-owned-in-request-schema': exact('no-server-owned-fields-in-schemas'),
    'openapi-request-body-optional': exact('request-bodies-required'),
    'openapi-route-hidden': specStatus('routes-cover-descriptor'),
    'spec-put-409-undeclared': exact('spec-declares-observed-statuses'),
    'spec-response-schema-wrong-type': exact('spec-response-bodies-match-schemas'),
    'guid-primary-key': exact('internal-key-int-hidden'),
    'support-spec-delete-204-undeclared': specStatus('put-and-delete-declare-no-conflicting-gaps'),
    'support-spec-response-has-undeclared-property': exact('spec-response-bodies-match-schemas'),
    'logistics-delete-404-on-repeat': exact('spec-declares-observed-statuses'),
  },
};

const memo = new Map<string, Promise<ApiResult>>();
const cached = (runner: ApiRunner) => ({ run: (domain: string, sources: string[]) => memo.get(domain + sources.join('\0')) ?? memo.set(domain + sources.join('\0'), runner.run(domain, sources)).get(domain + sources.join('\0'))! });
const failingIds = (failures: string[]) => [...new Set(failures.map((f) => f.split(': ')).map((p) => (p[0]!.startsWith('api scenario') ? p[1]! : p[0]!.split(' ')[2]!)))];

for (const home of ['idempotent-writes', 'api-design']) {
  const refs = JSON.parse(readFileSync(`${home}/evals/references.json`, 'utf8')) as Record<string, { good: string; bad: Record<string, string> }>;

  test(`${home}: every case has a reference solution and at least 8 bad references with a named reason`, async () => {
    const cases = await loadCases(`${home}/evals/cases.json`);
    assert.deepEqual(Object.keys(refs).sort(), cases.map((c) => c.id).sort());
    const bad = Object.entries(refs).filter(([id]) => !cases.find((c) => c.id === id)!.targets.length).flatMap(([, r]) => Object.keys(r.bad));
    assert.ok(bad.length >= 8, `${home} has ${bad.length} bad references`);
    assert.deepEqual(bad.filter((name) => !apiReasons[home]![name]), [], 'every bad reference needs a named reason');
    assert.deepEqual(Object.keys(apiReasons[home]!).filter((name) => !bad.includes(name)), [], 'every named reason needs a bad reference');
  });

  test(`${home}: graders accept the good reference and reject every bad one for its named reason`, { skip: !existsSync(apiDll) && 'build with: npm run build:api' }, async () => {
    const cases = (await loadCases(`${home}/evals/cases.json`)).filter((c) => c.targets.length === 0);
    const runner = new ApiRunner(apiDll, undefined, 4);
    const api = cached(runner);
    const grade = async (c: (typeof cases)[number], text: string) => apiFailures(c, await api.run(c.api!.domain, sourcesOf(text)));
    try {
      await Promise.all(cases.map(async (c) => {
        const ref = refs[c.id]!;
        assert.deepEqual(await grade(c, ref.good), [], `${c.id}: good reference should pass`);
        await Promise.all(Object.entries(ref.bad).map(async ([name, code]) => {
          const failures = await grade(c, code);
          const want = apiReasons[home]![name]!;
          const got = failingIds(failures);
          assert.ok(want.ids.every((id) => got.includes(id)), `${c.id}: "${name}" should fail ${want.ids.join(', ')}, got ${got.join(', ')}`);
          if (want.exact) assert.deepEqual([...got].sort(), [...want.ids].sort(), `${c.id}: "${name}" should fail only its named checks`);
        }));
      }));
    } finally {
      runner.close();
    }
  });
}

interface LineSpec { id: string; owns: string[]; monitors?: string[]; mutants: Record<string, string[]> }
interface MutantSpec { name: string; expectedFailures: string[]; staticExpectedFailures?: string[]; lintExpectedFailures?: string[]; specExpectedFailures?: string[] }
const failingOf = (m: MutantSpec) => [
  ...m.expectedFailures.map((i) => `scenario:${i}`), ...(m.staticExpectedFailures ?? []).map((i) => `static:${i}`),
  ...(m.lintExpectedFailures ?? []).map((i) => `lint:${i}`), ...(m.specExpectedFailures ?? []).map((i) => `spec:${i}`),
];

for (const home of ['idempotent-writes', 'api-design']) {
  const refs = JSON.parse(readFileSync(`${home}/evals/references.json`, 'utf8')) as Record<string, { good: string; bad: Record<string, string> }>;
  const lines = JSON.parse(readFileSync(`${home}/evals/lines.json`, 'utf8')) as LineSpec[];
  const mutants = new Map((JSON.parse(readFileSync('idempotent-writes/evals/mutants.json', 'utf8')) as MutantSpec[]).map((m) => [m.name, m]));

  test(`${home}: every targeted case has a good reference and a bad one for each of its line's mutants, each failing the line's owned checks`, async () => {
    const cases = (await loadCases(`${home}/evals/cases.json`)).filter((c) => c.targets.length > 0);
    assert.equal(cases.length, lines.length * 3);
    for (const c of cases) {
      const line = lines.find((l) => l.id === c.targets[0])!;
      assert.deepEqual(Object.keys(refs[c.id]!.bad), line.mutants[c.api!.domain], `${c.id}: bad references are the line's mutants of the domain`);
      const owned = new Set([...line.owns, ...(line.monitors ?? [])]);
      for (const name of Object.keys(refs[c.id]!.bad)) assert.ok(failingOf(mutants.get(name)!).some((id) => line.owns.includes(id)), `${c.id}: mutant ${name} must fail an owned check`);
      assert.deepEqual(c.api!.graded, [...line.owns, ...(line.monitors ?? [])]);
      assert.ok(owned.size > 0);
    }
  });

  test(`${home}: targeted cases accept the good reference and every bad one fails exactly the owned checks it breaks`, { skip: !existsSync(apiDll) && 'build with: npm run build:api' }, async () => {
    const cases = (await loadCases(`${home}/evals/cases.json`)).filter((c) => c.targets.length > 0);
    const runner = new ApiRunner(apiDll, undefined, 4);
    const api = cached(runner);
    const grade = async (c: (typeof cases)[number], text: string) => apiFailures(c, await api.run(c.api!.domain, sourcesOf(text)));
    try {
      await Promise.all(cases.map(async (c) => {
        const line = lines.find((l) => l.id === c.targets[0])!;
        const owned = new Set([...line.owns, ...(line.monitors ?? [])]);
        assert.deepEqual(await grade(c, refs[c.id]!.good), [], `${c.id}: good reference should pass`);
        await Promise.all(Object.entries(refs[c.id]!.bad).map(async ([name, code]) => {
          const want = failingOf(mutants.get(name)!).filter((id) => owned.has(id)).map((id) => id.split(':')[1]!).sort();
          const got = [...failingIds(await grade(c, code))].sort();
          assert.deepEqual(got, want, `${c.id}: "${name}" should fail exactly the owned checks it breaks`);
          assert.ok(got.length > 0, `${c.id}: "${name}" must fail the line`);
        }));
      }));
    } finally {
      runner.close();
    }
  });
}
