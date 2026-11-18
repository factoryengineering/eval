import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { loadCases } from '../src/cases.js';
import { annotate, parseSkill, renderSkill } from '../src/skill.js';
import type { Config } from '../src/types.js';

for (const file of ['eval.config.json', 'unit-testing/eval.config.json', 'idempotent-writes/eval.config.json', 'api-design/eval.config.json']) {
  const config = JSON.parse(readFileSync(file, 'utf8')) as Config;

  test(`${file}: the shipped skill renders byte-identically from its tagged form`, () => {
    const shipped = readFileSync(config.skill, 'utf8');
    const ids = JSON.parse(readFileSync(config.ids, 'utf8')) as Record<string, string>;
    assert.equal(renderSkill(annotate(shipped, ids)), shipped);
  });

  test(`${file}: every skill line has at least one targeted case`, async () => {
    const shipped = readFileSync(config.skill, 'utf8');
    const ids = JSON.parse(readFileSync(config.ids, 'utf8')) as Record<string, string>;
    const cases = await loadCases(config.cases);
    for (const line of parseSkill(annotate(shipped, ids)).lines)
      assert.ok(cases.some((c) => c.targets.includes(line.id)), `no case targets ${line.id}`);
  });
}

test('unit-testing trigger spec: valid split and the description is one of the variants under test', async () => {
  const { splitProblems, descriptionOf, variantsOf } = await import('../src/trigger.js');
  const config = JSON.parse(readFileSync('unit-testing/eval.config.json', 'utf8')) as Config & { trigger: string };
  const spec = JSON.parse(readFileSync(config.trigger, 'utf8')) as import('../src/trigger.js').TriggerSpec;
  assert.deepEqual(splitProblems(spec), []);
  const description = descriptionOf(readFileSync(config.skill, 'utf8'));
  assert.ok(variantsOf(spec).some((v) => v.description === description), 'shipped description is not a measured variant');
  for (const c of spec.cases.filter((x) => !x.expect)) assert.ok(c.targets.length > 0, `${c.id} should target a clause`);
});
