import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DEFAULT_MONITOR, criticalFailures, detectionPower, isSignal, trialsNeeded } from '../src/monitor.js';

test('trialsNeeded is the smallest n that detects a true 0.3 failure rate with 0.8 power', () => {
  const n = trialsNeeded(DEFAULT_MONITOR);
  assert.equal(n, 14);
  assert.ok(detectionPower(n, DEFAULT_MONITOR) >= 0.8);
  assert.ok(detectionPower(n - 1, DEFAULT_MONITOR) < 0.8);
});

test('14 pooled trials signal at 3 failures, never at 2', () => {
  assert.equal(criticalFailures(14, DEFAULT_MONITOR), 3);
  assert.equal(isSignal(2, 14, DEFAULT_MONITOR), false);
  assert.equal(isSignal(3, 14, DEFAULT_MONITOR), true);
});

test('a true 0.2 failure rate is not reliably detected at 14 trials', () => {
  assert.ok(detectionPower(14, { ...DEFAULT_MONITOR, effect: 0.2 }) < 0.6);
});
