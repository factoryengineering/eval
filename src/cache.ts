import { createHash } from 'node:crypto';
import { appendFile, mkdir, readFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { Completion, ModelSpec } from './types.js';

export type TrialComplete = (spec: ModelSpec, system: string, prompt: string, trial: number) => Promise<Completion>;

const keyOf = (spec: ModelSpec, system: string, prompt: string, trial: number) =>
  createHash('sha256').update(JSON.stringify([spec.id, spec.model, system, prompt, trial])).digest('hex');

// Completion cache keyed by model, system, prompt and trial index, so homes with byte-identical prompts generate once.
export async function cachedComplete(file: string, inner: TrialComplete): Promise<TrialComplete & { hits: () => number; misses: () => number }> {
  const memo = new Map<string, Promise<Completion>>();
  for (const line of (await readFile(file, 'utf8').catch(() => '')).split('\n').filter(Boolean)) {
    const { key, completion } = JSON.parse(line) as { key: string; completion: Completion };
    memo.set(key, Promise.resolve(completion));
  }
  await mkdir(dirname(file), { recursive: true });
  let hits = 0;
  let misses = 0;
  const fn: TrialComplete = (spec, system, prompt, trial) => {
    const key = keyOf(spec, system, prompt, trial);
    const have = memo.get(key);
    if (have) { hits++; return have; }
    misses++;
    const p = inner(spec, system, prompt, trial).then(async (completion) => {
      await appendFile(file, JSON.stringify({ key, model: spec.id, trial, at: new Date().toISOString(), completion }) + '\n');
      return completion;
    });
    p.catch(() => memo.delete(key));
    memo.set(key, p);
    return p;
  };
  return Object.assign(fn, { hits: () => hits, misses: () => misses });
}
