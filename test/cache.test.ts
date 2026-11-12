import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { cachedComplete } from '../src/cache.js';
import type { ModelSpec } from '../src/types.js';

const spec: ModelSpec = { id: 'm', provider: 'mock', model: 'm' };

test('completion cache generates once per model, prompt and trial, and persists across instances', async () => {
  const file = join(await mkdtemp(join(tmpdir(), 'cache-')), 'c.jsonl');
  let calls = 0;
  const inner = async (_s: ModelSpec, _sys: string, p: string, n: number) => ({ text: `${p}#${n}#${++calls}`, inputTokens: 1, outputTokens: 1, estimated: true });
  const a = await cachedComplete(file, inner);
  const [x, y, z] = await Promise.all([a(spec, '', 'p', 0), a(spec, '', 'p', 0), a(spec, '', 'p', 1)]);
  assert.equal(x.text, y.text);
  assert.notEqual(x.text, z.text);
  assert.equal(calls, 2);
  const b = await cachedComplete(file, inner);
  assert.equal((await b(spec, '', 'p', 1)).text, z.text);
  await b(spec, '', 'q', 0);
  assert.equal(calls, 3);
  assert.equal(b.hits(), 1);
  assert.equal((await readFile(file, 'utf8')).trim().split('\n').length, 3);
});

test('completion cache records a final failure and replays it instead of retrying', async () => {
  const file = join(await mkdtemp(join(tmpdir(), 'cache-')), 'c.jsonl');
  let calls = 0;
  const inner = async (): Promise<never> => { calls++; throw new Error('boom'); };
  const a = await cachedComplete(file, inner);
  await assert.rejects(a(spec, '', 'p', 0), /boom/);
  await new Promise((r) => setTimeout(r, 50));
  const b = await cachedComplete(file, inner);
  await assert.rejects(b(spec, '', 'p', 0), /boom/);
  assert.equal(calls, 1);
});
