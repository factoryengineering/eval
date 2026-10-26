import type { EvalCase, LineResult } from './types.js';

export function grade(c: EvalCase, output: string): string[] {
  const failures: string[] = [];
  for (const p of c.must) if (!new RegExp(p).test(output)) failures.push(`missing /${p}/`);
  for (const p of c.mustNot ?? []) if (new RegExp(p).test(output)) failures.push(`forbidden /${p}/`);
  return failures;
}

export function verdict(withRate: number, withoutRate: number, minDelta: number): LineResult['verdict'] {
  return withRate - withoutRate >= minDelta ? 'load-bearing' : 'not-load-bearing';
}
