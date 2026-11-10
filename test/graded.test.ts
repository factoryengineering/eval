import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testFailures } from '../src/exec.js';
import type { TestExecResult } from '../src/exec.js';
import { runEval } from '../src/runner.js';
import type { Config, EvalCase } from '../src/types.js';

const facts = { testMethods: ['Plain'], helperMethods: [], discardUsings: 0, connectionsOpenedInTests: 1, identifiers: [] };
const result = (over: Partial<TestExecResult>): TestExecResult =>
  ({ id: '1', compiled: true, diagnostics: [], forbidden: [], tests: [{ name: 'T.A', passed: true, error: null }], mutants: [], facts, ...over });
const spec = (graded?: string[]): EvalCase =>
  ({ id: 'c', prompt: '', targets: [], must: [], tests: { subject: '', mutants: [], rules: ['given-when-then', 'no-connection-in-tests'], graded } });

test('without graded every failing check counts', () => {
  const failures = testFailures(spec(), result({}));
  assert.equal(failures.length, 2);
});

test('graded keeps only the targeted check, so other style failures do not pollute a line', () => {
  assert.deepEqual(testFailures(spec(['rule given-when-then']), result({})).map((f) => f.split(':')[0]), ['rule given-when-then']);
});

test('graded test failed ignores style rules but still fails a failing test', () => {
  const failing = result({ tests: [{ name: 'T.A', passed: false, error: 'DbUpdateException: x' }] });
  assert.deepEqual(testFailures(spec(['test failed']), result({})), []);
  assert.equal(testFailures(spec(['test failed']), failing).length, 1);
});

test('graded never hides compile errors', () => {
  assert.deepEqual(testFailures(spec(['test failed']), result({ compiled: false, diagnostics: ['CS0103 x'] })), ['compile: CS0103 x']);
});

test('runEval prefetches in parallel and returns the same trials as sequential', async () => {
  const config: Config = { skill: '', ids: '', cases: '', trials: 2, minDelta: 0.3, models: [{ id: 'm', provider: 'mock', model: 'm' }] };
  const cases: EvalCase[] = [{ id: 'c', prompt: 'p', targets: ['a'], must: ['Alpha'] }];
  const skill = '# s\n- [a] Use `Alpha()`.\n';
  const a = await runEval(config, skill, cases);
  const b = await runEval(config, skill, cases, undefined, undefined, undefined, 4);
  assert.deepEqual(b.summary.lines, a.summary.lines);
});
