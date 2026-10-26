import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSystem, clauseVerdicts, descriptionOf, gradeTrigger, parseSelection, rates, runTrigger, variantsOf } from '../src/trigger.js';
import type { TriggerSpec } from '../src/trigger.js';

const spec: TriggerSpec = {
  skillName: 'me',
  distractors: [{ name: 'a', description: 'A.' }, { name: 'b', description: 'B.' }],
  clauses: [{ id: 'x', text: 'Handles X.' }, { id: 'y', text: 'Not Y.' }],
  baseline: 'Base.',
  cases: [
    { id: 'p', prompt: 'do x', expect: true, targets: ['x'] },
    { id: 'n', prompt: 'do y', expect: false, targets: ['y'] },
  ],
};

test('variantsOf builds none, baseline, full and one ablation per clause', () => {
  const v = variantsOf(spec);
  assert.deepEqual(v.map((x) => x.id), ['none', 'baseline', 'full', 'no-x', 'no-y']);
  assert.equal(v.find((x) => x.id === 'full')!.description, 'Handles X. Not Y.');
  assert.equal(v.find((x) => x.id === 'no-x')!.description, 'Not Y.');
  assert.equal(variantsOf(spec, 'Other.').at(-1)!.id, 'current');
  assert.equal(variantsOf(spec, 'Base.').some((x) => x.id === 'current'), false);
});

test('buildSystem lists every skill and a name-only entry for an empty description', () => {
  const s = buildSystem(spec, '');
  assert.match(s, /- \*\*me\*\*:$/m);
  assert.match(s, /- \*\*a\*\*: A\./);
});

test('parseSelection reads the JSON array, ignoring prose', () => {
  assert.deepEqual(parseSelection('I would use ["me"].'), ['me']);
  assert.deepEqual(parseSelection('[]'), []);
  assert.equal(parseSelection('none'), null);
});

test('gradeTrigger fails unparsable answers whatever the expectation', () => {
  assert.equal(gradeTrigger('me', true, ['me']), true);
  assert.equal(gradeTrigger('me', false, []), true);
  assert.equal(gradeTrigger('me', false, null), false);
});

test('descriptionOf reads the frontmatter description', () => {
  assert.equal(descriptionOf('---\nname: me\ndescription: Hello there.\n---\nbody'), 'Hello there.');
});

test('a clause is load bearing when removing it breaks its targeted cases', async () => {
  const fake = async (_m: unknown, system: string, prompt: string) => {
    const yes = prompt.startsWith('do x') ? system.includes('Handles X.') : !system.includes('Not Y.');
    return { text: yes ? '["me"]' : '[]', inputTokens: 1, outputTokens: 1, estimated: false };
  };
  const { trials } = await runTrigger(spec, [{ id: 'm', provider: 'mock', model: 'm' }], 2, undefined, fake);
  const verdicts = clauseVerdicts(rates(trials, spec), spec, 0.3);
  assert.deepEqual(verdicts.map((v) => [v.clause, v.withRate, v.withoutRate, v.verdict]), [
    ['x', 1, 0, 'load-bearing'],
    ['y', 1, 0, 'load-bearing'],
  ]);
});
