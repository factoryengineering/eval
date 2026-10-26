import assert from 'node:assert/strict';
import { test } from 'node:test';
import { grade } from '../src/grade.js';
import { runEval } from '../src/runner.js';
import { annotate, parseSkill, renderSkill } from '../src/skill.js';
import { compare } from '../src/store.js';
import type { Config, EvalCase } from '../src/types.js';

const skill = '# s\n- [a] Use `Alpha()`.\n- [b] Use `Beta()`.\n';
const cases: EvalCase[] = [{ id: 'c', prompt: 'p', targets: ['a'], must: ['Alpha'] }];
const config: Config = { skill: '', ids: '', cases: '', trials: 2, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };

test('parseSkill finds ids and renderSkill omits/strips them', () => {
  assert.deepEqual(parseSkill(skill).lines.map((l) => l.id), ['a', 'b']);
  assert.equal(renderSkill(skill, 'a').includes('Alpha'), false);
  assert.equal(renderSkill(skill).includes('[a]'), false);
});

test('annotate tags clean bullets from the sidecar and renders back to the exact shipped text', () => {
  const clean = '# s\n- Use `Alpha()`.\n- Use `Beta()`.\n';
  const ids = { a: 'Use `Alpha()`.', b: 'Use `Beta()`.' };
  const tagged = annotate(clean, ids);
  assert.deepEqual(parseSkill(tagged).lines.map((l) => l.id), ['a', 'b']);
  assert.equal(renderSkill(tagged), clean);
  assert.equal(renderSkill(tagged, 'a'), '# s\n- Use `Beta()`.\n');
});

test('annotate fails loudly when the skill and sidecar drift apart', () => {
  assert.throws(() => annotate('- Use X.\n- Extra.\n', { x: 'Use X.' }), /no id for skill line: Extra\./);
  assert.throws(() => annotate('- Use X.\n', { x: 'Use X.', y: 'Gone.' }), /id y not found in skill/);
});

test('grade reports missing and forbidden patterns', () => {
  const c: EvalCase = { id: 'x', prompt: '', targets: [], must: ['A'], mustNot: ['B'] };
  assert.deepEqual(grade(c, 'AB'), ['forbidden /B/']);
  assert.deepEqual(grade(c, ''), ['missing /A/']);
});

test('grade checks only fenced code when present, so prose mentions do not count', () => {
  const c: EvalCase = { id: 'x', prompt: '', targets: [], must: ['A'], mustNot: ['B'] };
  assert.deepEqual(grade(c, 'I avoided B.\n```csharp\nA\n```'), []);
  assert.deepEqual(grade(c, 'text\n```csharp\nA B\n```'), ['forbidden /B/']);
  assert.deepEqual(grade(c, 'A\nB'), ['forbidden /B/']);
});

test('runEval marks a line load-bearing when removing it breaks the case', async () => {
  const fake = async (_s: unknown, system: string) => ({
    text: system.includes('Alpha') ? 'Alpha' : 'nope', inputTokens: 10, outputTokens: 2, estimated: false,
  });
  const { summary } = await runEval(config, skill, cases, fake);
  assert.equal(summary.lines.length, 1);
  assert.equal(summary.lines[0]!.verdict, 'load-bearing');
  assert.equal(summary.lines[0]!.delta, 1);
  assert.equal(summary.tokens.m!.calls, 4);
});

test('guard cases run with the full skill and never affect line deltas', async () => {
  const fake = async (_s: unknown, system: string) => ({
    text: system.includes('Alpha') ? 'Alpha' : 'nope', inputTokens: 10, outputTokens: 2, estimated: false,
  });
  const guard: EvalCase = { id: 'g', prompt: 'p', targets: [], guard: true, must: ['Alpha'] };
  const { summary, trials } = await runEval(config, skill, [...cases, guard], fake);
  assert.equal(summary.lines[0]!.delta, 1);
  assert.deepEqual(summary.guards, [{ model: 'm', caseId: 'g', rate: 1, n: 2 }]);
  assert.equal(trials.filter((t) => t.line === '(guard)').length, 2);
  const filtered = await runEval({ ...config, lines: ['a'] }, skill, [...cases, guard], fake);
  assert.equal(filtered.summary.guards?.length ?? 0, 0);
});

test('runEval rejects cases targeting unknown lines', async () => {
  await assert.rejects(runEval(config, skill, [{ ...cases[0]!, targets: ['zzz'] }]), /unknown line/);
});

test('compare reports token deltas', async () => {
  const fake = async () => ({ text: '', inputTokens: 10, outputTokens: 2, estimated: false });
  const a = (await runEval(config, skill, cases, fake)).summary;
  const b = structuredClone(a);
  b.tokens.m!.input += 5;
  assert.match(compare(a, b), /input \+5/);
});
