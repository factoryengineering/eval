import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { loadCases } from '../src/cases.js';
import { ExecRunner, makeGrader } from '../src/exec.js';

const dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll';
const fence = (code: string) => '```csharp\n' + code + '\n```';
const reasons: Record<string, string> = {
  'failing-assertion': 'test failed',
  'helper-async-suffix': 'rule gherkin-helpers',
  'helper-not-gherkin': 'rule gherkin-helpers',
  'plain-test-names': 'rule given-when-then',
  'weak-single-post': 'mutant survived',
  'connection-in-test': 'rule no-connection-in-tests',
  'discard-usings': 'rule no-discard-usings',
};
const suites = [
  { name: 'ef-linq', cases: 'evals/cases.json', references: 'evals/references.json' },
  { name: 'unit-testing', cases: 'unit-testing/evals/cases.json', references: 'unit-testing/evals/references.json' },
];

for (const suite of suites) {
  const refs = JSON.parse(readFileSync(suite.references, 'utf8')) as Record<string, { good: string; bad: Record<string, string> }>;

  test(`${suite.name}: every case has a reference solution`, async () => {
    const cases = await loadCases(suite.cases);
    assert.deepEqual(Object.keys(refs).sort(), cases.map((c) => c.id).sort());
  });

  test(`${suite.name}: graders accept the good reference and reject every bad one`, { skip: !existsSync(dll) && 'build with: npm run build:runner' }, async () => {
    const cases = await loadCases(suite.cases);
    const runner = new ExecRunner(dll);
    try {
      const grade = makeGrader(runner, { bad: async (ids) => ids.filter((id) => id.length < 2) });
      for (const c of cases) {
        const ref = refs[c.id]!;
        assert.deepEqual(await grade(c, fence(ref.good)), [], `${c.id}: good reference should pass`);
        for (const [name, code] of Object.entries(ref.bad)) {
          const failures = await grade(c, fence(code));
          assert.notDeepEqual(failures, [], `${c.id}: bad reference "${name}" should fail`);
          const reason = suite.name === 'unit-testing' ? reasons[name] : undefined;
          if (reason) assert.ok(failures.every((f) => f.startsWith(reason)), `${c.id}: "${name}" should fail only for "${reason}", got ${failures.join(' | ')}`);
        }
      }
    } finally {
      runner.close();
    }
  });
}
