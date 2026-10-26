import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { test } from 'node:test';
import { ExecRunner, execFailures, makeGrader } from '../src/exec.js';
import type { ExecResult } from '../src/exec.js';
import { runEval } from '../src/runner.js';
import type { Config, EvalCase } from '../src/types.js';

const ok: ExecResult = {
  id: '0', compiled: true, diagnostics: [], forbidden: [], invoked: [{ method: 'G.M', error: null }],
  sql: ['SELECT "p"."Title" FROM "Posts" AS "p" WHERE "p"."Views" > 100'], queryStrings: [], syntax: null,
};
const c = (exec: EvalCase['exec']): EvalCase => ({ id: 'x', prompt: '', targets: [], must: [], exec });

test('execFailures reports compile, forbidden, runtime and sql problems in priority order', () => {
  assert.deepEqual(execFailures(c({}), { ...ok, compiled: false, diagnostics: ['CS1: bad'] }), ['compile: CS1: bad']);
  assert.deepEqual(execFailures(c({}), { ...ok, forbidden: ['System.IO.File'] }), ['forbidden api: System.IO.File']);
  assert.deepEqual(execFailures(c({}), { ...ok, invoked: [] }), ['no public method was invoked']);
  assert.deepEqual(execFailures(c({}), { ...ok, invoked: [{ method: 'G.M', error: 'InvalidOperationException: x' }] }), ['runtime G.M: InvalidOperationException: x']);
  assert.deepEqual(execFailures(c({ sqlMust: ['WHERE', 'ORDER BY'] }), ok), ['sql missing /ORDER BY/']);
  assert.deepEqual(execFailures(c({ sqlMustNot: ['"Id"'] }), ok), []);
  assert.deepEqual(execFailures(c({ sqlMustNot: ['"Title"'] }), ok), ['sql forbidden /"Title"/']);
  assert.deepEqual(execFailures(c({ minCommands: 3 }), ok), ['sql commands 1 < 3']);
});

test('runEval uses the injected async grader', async () => {
  const skill = '- [a] Use Alpha.\n';
  const cfg: Config = { skill: '', ids: '', cases: '', trials: 1, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };
  const complete = async (_m: unknown, system: string) => ({ text: system.includes('Alpha') ? 'yes' : 'no', inputTokens: 1, outputTokens: 1, estimated: false });
  const grader = async (_c: EvalCase, text: string) => (text === 'yes' ? [] : ['nope']);
  const { summary } = await runEval(cfg, skill, [{ id: 'k', prompt: 'p', targets: ['a'], must: [] }], complete, undefined, grader);
  assert.equal(summary.lines[0]!.delta, 1);
});

test('guard cases also use the injected grader', async () => {
  const cfg: Config = { skill: '', ids: '', cases: '', trials: 1, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };
  const complete = async () => ({ text: 'x', inputTokens: 1, outputTokens: 1, estimated: false });
  const guard: EvalCase = { id: 'g', prompt: 'p', targets: [], guard: true, must: [] };
  const { summary } = await runEval(cfg, '- [a] A.\n', [guard], complete, undefined, async () => ['sql missing /UPDATE/']);
  assert.deepEqual(summary.guards, [{ model: 'm', caseId: 'g', rate: 0, n: 1 }]);
});

const dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll';
test('ExecRunner end to end against the real .NET runner', { skip: !existsSync(dll) && 'build with: npm run build:runner' }, async () => {
  const runner = new ExecRunner(dll);
  try {
    const grade = makeGrader(runner);
    const good = '```csharp\npublic async Task<List<string>> T(BlogContext db) { var q = from p in db.Posts where p.Views > 100 select p.Title; return await q.ToListAsync(); }\n```';
    const bad = '```csharp\npublic async Task<List<Post>> T(BlogContext db, string x) => (await db.Posts.ToListAsync()).Where(p => TitleRules.HasPrefix(p.Title, x)).ToList();\n```';
    const spec = c({ sqlMust: ['WHERE'] });
    assert.deepEqual(await grade(spec, good), []);
    assert.deepEqual(await grade(spec, bad), ['sql missing /WHERE/']);
    assert.match((await grade(spec, 'not code at all'))[0]!, /^compile:/);
  } finally {
    runner.close();
  }
});
