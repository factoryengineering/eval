import { spawn } from 'node:child_process';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface } from 'node:readline';
import { codeOf, grade } from './grade.js';
import type { EvalCase } from './types.js';

export interface ExecResult {
  id: string;
  compiled: boolean;
  diagnostics: string[];
  forbidden: string[];
  invoked: { method: string; error: string | null }[];
  sql: string[];
  queryStrings: string[];
  syntax: { queryExpressions: number; methodCalls: string[]; queryableOperators: string[]; identifiers: string[] } | null;
}

const failure = (id: string, message: string): ExecResult =>
  ({ id, compiled: false, diagnostics: [message], forbidden: [], invoked: [], sql: [], queryStrings: [], syntax: null });

export class ExecRunner {
  private child?: ChildProcessWithoutNullStreams;
  private pending = new Map<string, (r: ExecResult) => void>();
  private n = 0;

  constructor(private dll = 'dotnet/EvalRunner/bin/Release/net10.0/EvalRunner.dll') {}

  private start(): ChildProcessWithoutNullStreams {
    const child = spawn('dotnet', [this.dll], { stdio: ['pipe', 'pipe', 'pipe'] });
    createInterface({ input: child.stdout }).on('line', (line) => {
      const r = JSON.parse(line) as ExecResult;
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
  return failures;
}

export interface NameChecker {
  bad(ids: string[]): Promise<string[]>;
}

export const makeGrader = (runner: ExecRunner, judge?: NameChecker) => async (c: EvalCase, text: string): Promise<string[]> => {
  const failures = grade(c, text);
  if (!c.exec) return failures;
  const r = await runner.run(codeOf(text), c.exec.args);
  failures.push(...execFailures(c, r));
  if (c.exec.judgeNames && judge && r.compiled && r.syntax) {
    const bad = await judge.bad(r.syntax.identifiers);
    if (bad.length) failures.push(`names not meaningful: ${bad.join(', ')}`);
  }
  return failures;
};
