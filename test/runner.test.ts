import assert from 'node:assert/strict';
import { test } from 'node:test';
import { grade } from '../src/grade.js';
import { runEval } from '../src/runner.js';
import { parseSkill, renderSkill } from '../src/skill.js';
import { compare } from '../src/store.js';
import type { Config, EvalCase } from '../src/types.js';

const skill = '# s\n- [a] Use `Alpha()`.\n- [b] Use `Beta()`.\n';
const cases: EvalCase[] = [{ id: 'c', prompt: 'p', targets: ['a'], must: ['Alpha'] }];
const config: Config = { skill: '', cases: '', trials: 2, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };

test('parseSkill finds ids and renderSkill omits/strips them', () => {
  assert.deepEqual(parseSkill(skill).lines.map((l) => l.id), ['a', 'b']);
  assert.equal(renderSkill(skill, 'a').includes('Alpha'), false);
  assert.equal(renderSkill(skill).includes('[a]'), false);
});

test('grade reports missing and forbidden patterns', () => {
  const c: EvalCase = { id: 'x', prompt: '', targets: [], must: ['A'], mustNot: ['B'] };
  assert.deepEqual(grade(c, 'AB'), ['forbidden /B/']);
  assert.deepEqual(grade(c, ''), ['missing /A/']);
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
