import { spawn } from 'node:child_process';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface } from 'node:readline';
import { bucketOf } from './baseline.js';
import { codeOf, grade } from './grade.js';
import type { EvalCase, MutantSpec } from './types.js';

export interface ExecResult {
  id: string;
  compiled: boolean;
  diagnostics: string[];
  forbidden: string[];
  invoked: { method: string; error: string | null }[];
  sql: string[];
  queryStrings: string[];
  syntax: {
    queryExpressions: number;
    methodCalls: string[];
    queryableOperators: string[];
    identifiers: string[];
    awaitedQueryLocals: number;
    inlineAwaitedQueries: number;
  } | null;
}

export interface TestExecResult {
  id: string;
  compiled: boolean;
  diagnostics: string[];
  forbidden: string[];
  tests: { name: string; passed: boolean; error: string | null }[];
  mutants: { name: string; status: 'killed' | 'survived' | 'invalid' | 'uncompilable' }[];
  facts: {
    testMethods: string[];
    helperMethods: string[];
    discardUsings: number;
    connectionsOpenedInTests: number;
    identifiers: string[];
  } | null;
}

const failure = (id: string, message: string): ExecResult & TestExecResult =>
  ({ id, compiled: false, diagnostics: [message], forbidden: [], invoked: [], sql: [], queryStrings: [], syntax: null, tests: [], mutants: [], facts: null });

export class ExecRunner {
  private child?: ChildProcessWithoutNullStreams;
  private pending = new Map<string, (r: ExecResult & TestExecResult) => void>();
  private n = 0;

  constructor(private dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll') {}

  private start(): ChildProcessWithoutNullStreams {
    const child = spawn('dotnet', [this.dll], { stdio: ['pipe', 'pipe', 'pipe'] });
    createInterface({ input: child.stdout }).on('line', (line) => {
      const r = JSON.parse(line) as ExecResult & TestExecResult;
      this.pending.get(r.id)?.(r);
      this.pending.delete(r.id);
    });
    child.stderr.resume();
    const drop = (why: string) => {
      if (this.child === child) this.child = undefined;
      for (const [id, resolve] of this.pending) resolve(failure(id, why));
      this.pending.clear();
    };
    child.on('exit', (code) => drop(`runner exited (${code})`));
    child.on('error', (e) => drop(`runner failed to start: ${e.message}`));
    return child;
  }

  run(code: string, args: { string?: string; int?: number } = {}): Promise<ExecResult> {
    const id = String(this.n++);
    this.child ??= this.start();
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.child!.stdin.write(JSON.stringify({ id, code, stringArg: args.string, intArg: args.int }) + '\n');
    });
  }

  runTests(code: string, subject: string, mutants: MutantSpec[]): Promise<TestExecResult> {
    const id = String(this.n++);
    this.child ??= this.start();
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.child!.stdin.write(JSON.stringify({ id, code, subject, mutants }) + '\n');
    });
  }

  close() {
    this.child?.stdin.end();
    this.child = undefined;
  }
}

export function execFailures(c: EvalCase, r: ExecResult): string[] {
  const x = c.exec!;
  if (!r.compiled) return [`compile: ${r.diagnostics[0] ?? 'failed'}`];
  if (r.forbidden.length) return [`forbidden api: ${r.forbidden.join(', ')}`];
  if (!r.invoked.length) return ['no public method was invoked'];
  const failures = r.invoked.filter((i) => i.error).map((i) => `runtime ${i.method}: ${i.error}`);
  const sql = r.sql.join('\n');
  for (const p of x.sqlMust ?? []) if (!new RegExp(p, 'i').test(sql)) failures.push(`sql missing /${p}/`);
  for (const p of x.sqlMustNot ?? []) if (new RegExp(p, 'i').test(sql)) failures.push(`sql forbidden /${p}/`);
  if (x.minCommands !== undefined && r.sql.length < x.minCommands) failures.push(`sql commands ${r.sql.length} < ${x.minCommands}`);
  const banned = (x.noMethodSyntax ?? []).filter((op) => r.syntax?.queryableOperators.includes(op));
  if (banned.length) failures.push(`method syntax on a queryable: ${banned.join(', ')}`);
  if (x.separateQuery && r.syntax) {
    if (r.syntax.inlineAwaitedQueries > 0) failures.push('query executed inline instead of via a variable');
    else if (r.syntax.awaitedQueryLocals < 1) failures.push('no query variable was awaited');
  }
  return failures;
}

const GIVEN_WHEN_THEN = /^Given[A-Za-z0-9]+_When[A-Za-z0-9]+_Then[A-Za-z0-9]+$/;
const GHERKIN_HELPER = /^(Given|When|Then)[A-Za-z0-9]*$/;

export const ruleChecks: Record<string, (facts: NonNullable<TestExecResult['facts']>) => string | undefined> = {
  'given-when-then': (f) => {
    const bad = f.testMethods.filter((n) => !GIVEN_WHEN_THEN.test(n));
    return bad.length ? `test names not Given_When_Then: ${bad.join(', ')}` : undefined;
  },
  'gherkin-helpers': (f) => {
    const bad = f.helperMethods.filter((n) => !GHERKIN_HELPER.test(n) || n.endsWith('Async'));
    return bad.length ? `helpers not Gherkin-named or Async-suffixed: ${bad.join(', ')}` : undefined;
  },
  'no-discard-usings': (f) => (f.discardUsings > 0 ? `${f.discardUsings} discard using declaration(s)` : undefined),
  'no-connection-in-tests': (f) => (f.connectionsOpenedInTests > 0 ? `${f.connectionsOpenedInTests} connection(s) opened inside test methods` : undefined),
};

export function testFailures(c: EvalCase, r: TestExecResult): string[] {
  const x = c.tests!;
  if (!r.compiled) return [`compile: ${r.diagnostics[0] ?? 'failed'}`];
  if (r.forbidden.length) return [`forbidden api: ${r.forbidden.join(', ')}`];
  const min = x.minTests ?? 1;
  if (r.tests.length < min) return [`tests found ${r.tests.length} < ${min}`];
  const failures = [
    ...r.tests.filter((t) => !t.passed).map((t) => `test failed ${t.name}: ${t.error}`),
    ...r.mutants.filter((m) => m.status !== 'killed').map((m) => `mutant ${m.status}: ${m.name}`),
  ];
  for (const rule of x.rules ?? []) {
    const check = ruleChecks[rule];
    if (!check) throw new Error(`unknown rule check ${rule}`);
    const message = r.facts ? check(r.facts) : 'no facts';
    if (message) failures.push(`rule ${rule}: ${message}`);
  }
  if (!x.graded) return failures;
  const ignorable = (bucket: string) => bucket === 'test failed' || bucket === 'mutant survived' || bucket.startsWith('rule ');
  return failures.filter((f) => !ignorable(bucketOf(f)) || x.graded!.includes(bucketOf(f)));
}

export interface NameChecker {
  bad(ids: string[]): Promise<string[]>;
}

export const makeGrader = (runner: ExecRunner, judge?: NameChecker) => async (c: EvalCase, text: string): Promise<string[]> => {
  const failures = grade(c, text);
  if (c.tests) {
    failures.push(...testFailures(c, await runner.runTests(codeOf(text), c.tests.source!, c.tests.mutants)));
    return failures;
  }
  if (!c.exec) return failures;
  const r = await runner.run(codeOf(text), c.exec.args);
  failures.push(...execFailures(c, r));
  if (c.exec.judgeNames && judge && r.compiled && r.syntax) {
    const bad = await judge.bad(r.syntax.identifiers);
    if (bad.length) failures.push(`names not meaningful: ${bad.join(', ')}`);
  }
  return failures;
};
