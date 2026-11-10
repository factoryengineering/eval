import assert from 'node:assert/strict';
import { test } from 'node:test';
import { analysisReport, analyzeLines } from '../src/analyze.js';
import { fisherOneSided } from '../src/stats.js';
import type { Trial } from '../src/types.js';

test('fisherOneSided matches known values', () => {
  assert.ok(Math.abs(fisherOneSided(9, 10, 0, 10) - 0.0001) < 0.0001);
  assert.ok(Math.abs(fisherOneSided(10, 10, 5, 10) - 0.0163) < 0.0005);
  assert.equal(fisherOneSided(10, 10, 10, 10), 1);
});

const trial = (variant: 'with' | 'without', pass: boolean, line = 'l'): Trial =>
  ({ model: 'm', line, variant, caseId: 'c', trial: 0, pass, failures: [], inputTokens: 0, outputTokens: 0, estimated: true, output: '' });

test('analyzeLines separates guards and flags a line only when significant and large', () => {
  const trials = [
    ...Array.from({ length: 10 }, () => trial('with', true)),
    ...Array.from({ length: 10 }, () => trial('without', false)),
    ...Array.from({ length: 10 }, () => trial('with', true, 'flat')),
    ...Array.from({ length: 10 }, () => trial('without', true, 'flat')),
    { ...trial('with', false), line: '(guard)' },
  ];
  const rows = analyzeLines(trials);
  assert.equal(rows.length, 2);
  const report = analysisReport(rows);
  assert.match(report, /l +0\/10 -> 10\/10 .*LOAD-BEARING/);
  assert.match(report, /flat +10\/10 -> 10\/10 .*not significant/);
});
