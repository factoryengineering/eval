import assert from 'node:assert/strict';
import { test } from 'node:test';
import { agreement, judgeIdentifiers, offenders, parseVerdicts } from '../src/judge.js';
import type { ModelSpec } from '../src/types.js';

const spec: ModelSpec = { id: 'j', provider: 'mock', model: 'j' };
const reply = (text: string) => async () => ({ text, inputTokens: 1, outputTokens: 1, estimated: true });

test('parseVerdicts reads a JSON object from prose and ignores non-boolean values', () => {
  assert.deepEqual(parseVerdicts('Here: {"post": true, "p": false, "x": "maybe"}', ['post', 'p', 'x']), { post: true, p: false });
  assert.deepEqual(parseVerdicts('no json', ['a']), {});
});

test('judgeIdentifiers asks only about identifiers it has no verdict for', async () => {
  const asked: string[] = [];
  const complete = async (_s: ModelSpec, _sys: string, prompt: string) => {
    asked.push(prompt);
    return { text: '{"p": false, "total": true}', inputTokens: 1, outputTokens: 1, estimated: true };
  };
  const first = await judgeIdentifiers(['post', 'p', 'total'], { post: true }, spec, complete);
  assert.deepEqual(first, { post: true, p: false, total: true });
  assert.equal(asked.length, 1);
  assert.ok(!asked[0]!.includes('"post"') && asked[0]!.includes('"p"'));
  await judgeIdentifiers(['post', 'p'], first, spec, complete);
  assert.equal(asked.length, 1);
});

test('judgeIdentifiers batches and retries once for identifiers the judge skipped', async () => {
  let calls = 0;
  const complete = async () => {
    calls++;
    return { text: calls === 1 ? '{"a": true}' : '{"b": false}', inputTokens: 1, outputTokens: 1, estimated: true };
  };
  assert.deepEqual(await judgeIdentifiers(['a', 'b'], {}, spec, complete), { a: true, b: false });
  assert.equal(calls, 2);
});

test('offenders fails closed on identifiers with no verdict', () => {
  assert.deepEqual(offenders(['post', 'p', 'zzz'], { post: true, p: false }), ['p', 'zzz']);
  assert.deepEqual(offenders([], {}), []);
});

test('agreement reports counts and the disagreeing identifiers', async () => {
  const verdicts = await judgeIdentifiers(['a', 'b', 'c'], {}, spec, reply('{"a": true, "b": true, "c": false}'));
  assert.deepEqual(agreement({ a: true, b: false, c: false }, verdicts), { n: 3, agree: 2, disagreements: ['b'] });
});
