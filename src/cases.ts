import { readFile } from 'node:fs/promises';
import type { EvalCase } from './types.js';

export async function loadCases(file: string): Promise<EvalCase[]> {
  const cases = JSON.parse(await readFile(file, 'utf8')) as EvalCase[];
  for (const c of cases) {
    if (!c.tests) continue;
    c.tests.source = await readFile(c.tests.subject, 'utf8');
    c.prompt = c.prompt.replaceAll('{{subject}}', c.tests.source.trim());
  }
  return cases;
}
