import type { EvalCase, LineResult } from './types.js';

const FENCE = /```[^\n]*\n([\s\S]*?)```/g;

export function codeOf(output: string): string {
  const blocks = [...output.matchAll(FENCE)].map((m) => m[1]!);
  return blocks.length ? blocks.join('\n') : output;
}

export function grade(c: EvalCase, text: string): string[] {
  const output = codeOf(text);
  const failures: string[] = [];
  for (const p of c.must) if (!new RegExp(p).test(output)) failures.push(`missing /${p}/`);
  for (const p of c.mustNot ?? []) if (new RegExp(p).test(output)) failures.push(`forbidden /${p}/`);
  return failures;
}

export function verdict(withRate: number, withoutRate: number, minDelta: number): LineResult['verdict'] {
  return withRate - withoutRate >= minDelta ? 'load-bearing' : 'not-load-bearing';
}
