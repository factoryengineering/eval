import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { ExecRunner, makeGrader } from '../src/exec.js';
import type { EvalCase } from '../src/types.js';

const dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll';
const cases = JSON.parse(readFileSync('evals/cases.json', 'utf8')) as EvalCase[];
const refs = JSON.parse(readFileSync('evals/references.json', 'utf8')) as Record<string, { good: string; bad: Record<string, string> }>;
const fence = (code: string) => '```csharp\n' + code + '\n```';

test('every case has a reference solution', () => {
  assert.deepEqual(Object.keys(refs).sort(), cases.map((c) => c.id).sort());
});

test('graders accept the good reference and reject every bad one', { skip: !existsSync(dll) && 'build with: npm run build:runner' }, async () => {
  const runner = new ExecRunner(dll);
  try {
    const grade = makeGrader(runner, { bad: async (ids) => ids.filter((id) => id.length < 2) });
    for (const c of cases) {
      const ref = refs[c.id]!;
      assert.deepEqual(await grade(c, fence(ref.good)), [], `${c.id}: good reference should pass`);
      for (const [name, code] of Object.entries(ref.bad))
        assert.notDeepEqual(await grade(c, fence(code)), [], `${c.id}: bad reference "${name}" should fail`);
    }
  } finally {
    runner.close();
  }
});
