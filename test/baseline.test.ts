import assert from 'node:assert/strict';
import { test } from 'node:test';
import { bucketOf, report, runBaseline, summarize } from '../src/baseline.js';
import type { Config, EvalCase } from '../src/types.js';

const config: Config = { skill: '', ids: '', cases: '', trials: 2, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };
const cases: EvalCase[] = [{ id: 'c', prompt: 'p', targets: [], must: [] }];
const answer = (text: string) => async () => ({ text, inputTokens: 1, outputTokens: 1, estimated: true });

test('bucketOf groups failures by the check that produced them', () => {
  assert.equal(bucketOf('rule given-when-then: test names not Given_When_Then: A, B'), 'rule given-when-then');
  assert.equal(bucketOf('test failed T.X: EqualException'), 'test failed');
  assert.equal(bucketOf('mutant survived: boundary'), 'mutant survived');
  assert.equal(bucketOf('compile: CS0103 nope'), 'compile');
});

test('runBaseline sends no system prompt and summarizes pass rate and failure buckets', async () => {
  const systems: string[] = [];
  let n = 0;
  const complete = async (_spec: unknown, system: string) => {
    systems.push(system);
    return answer(`reply ${n++}`)();
  };
  const grade = async (_c: EvalCase, text: string) => (text === 'reply 0' ? [] : ['rule given-when-then: x', 'mutant survived: boundary']);
  const trials = await runBaseline(config, cases, grade, complete, undefined, 1);
  assert.deepEqual(systems, ['', '']);
  const [cell] = summarize(trials);
  assert.equal(cell!.passRate, 0.5);
  assert.deepEqual(cell!.buckets, { 'rule given-when-then': 0.5, 'mutant survived': 0.5 });
  assert.match(report([cell!]), /pass 0\.50 \(n=2\).*rule given-when-then 0\.50/);
});
