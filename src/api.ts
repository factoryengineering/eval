import { spawn } from 'node:child_process';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { readdir, readFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { CATALOG } from './api-catalog.js';
import type { EvalCase, GradeNotes } from './types.js';

export const API_PREAMBLE = 'Implement an HTTP API with ASP.NET Core controllers and EF Core (SQLite)';

export interface ApiCheck {
  layer: string;
  id: string;
  passed: boolean;
  evidence: string;
  group: string | null;
  advisory: boolean;
}

export interface ApiResult {
  id: string;
  compile: { ok: boolean; errors: string[] };
  checks: ApiCheck[];
  error?: string | null;
}

const FENCE = /```[^\n]*\n([\s\S]*?)```/g;

export const sourcesOf = (output: string): string[] => {
  const blocks = [...output.matchAll(FENCE)].map((m) => m[1]!);
  return blocks.length ? blocks : [output];
};

const oneLine = (s: string) => s.replace(/\s+/g, ' ').trim().slice(0, 300);

const covers = (graded: string[], c: ApiCheck) =>
  graded.some((g) => g === c.layer || g === c.id || g === `${c.layer}:${c.id}` || (c.group !== null && g === `${c.layer}:${c.group}`));

export const bucketName = (layer: string, id: string, group: string | null) => (layer === 'scenario' ? `api scenario ${group ?? id}` : `api ${layer} ${id}`);

export function apiFailures(c: EvalCase, r: ApiResult, notes?: GradeNotes): string[] {
  if (notes) notes.notes = [];
  if (r.error) return [`api error: ${oneLine(r.error)}`];
  if (!r.compile.ok) return [`api compile: ${oneLine(r.compile.errors[0] ?? 'failed')}`];
  const graded = c.api?.graded;
  const failing = r.checks.filter((k) => !k.passed);
  if (notes) {
    notes.notes = failing.filter((k) => k.advisory).map((k) => `api advisory ${k.id}: ${oneLine(k.evidence)}`);
    const create = r.checks.find((k) => k.layer === 'scenario' && k.id === 'create-succeeds');
    if (create && !create.passed) {
      const later = failing.filter((k) => !k.advisory && k.layer === 'scenario' && k.id !== create.id).length;
      notes.bootstrapNote = `create-succeeds failed (${oneLine(create.evidence)}); ${later} later scenario(s) failed and may cascade from it`;
    }
  }
  return failing
    .filter((k) => !k.advisory && (k.layer === 'host' || !graded || covers(graded, k)))
    .map((k) => (k.layer === 'host' ? `api host: ${oneLine(k.evidence)}` : `${bucketName(k.layer, k.id, k.group)}: ${k.layer === 'scenario' ? `${k.id}: ` : ''}${oneLine(k.evidence)}`));
}

export const apiBuckets = (graded: string[] | undefined): string[] => {
  const buckets = new Set<string>(['api compile', 'api host']);
  const all = [
    ...Object.entries(CATALOG.scenarios).map(([id, group]) => ({ layer: 'scenario', id, group })),
    ...(['static', 'lint', 'spec'] as const).flatMap((layer) => CATALOG[layer].map((id) => ({ layer, id, group: null }))),
  ].map((k) => ({ ...k, passed: false, evidence: '', advisory: false }));
  for (const k of all) if (!graded || covers(graded, k)) buckets.add(bucketName(k.layer, k.id, k.group));
  return [...buckets];
};

interface Worker {
  child: ChildProcessWithoutNullStreams;
  pending: Map<string, (r: ApiResult) => void>;
}

const failed = (id: string, error: string): ApiResult => ({ id, compile: { ok: false, errors: [] }, checks: [], error });

export class ApiRunner {
  private workers: Worker[] = [];
  private domains = new Map<string, Promise<unknown>>();
  private n = 0;

  constructor(
    private dll = 'dotnet/ApiConformance.Cli/bin/Release/net10.0/ApiConformance.Cli.dll',
    private domainDir = 'idempotent-writes/evals/domains',
    private size = 2,
    private timeoutMs = 300_000,
  ) {}

  private start(): Worker {
    const child = spawn('dotnet', [this.dll], { stdio: ['pipe', 'pipe', 'pipe'] });
    const worker: Worker = { child, pending: new Map() };
    createInterface({ input: child.stdout }).on('line', (line) => {
      const r = JSON.parse(line) as ApiResult;
      worker.pending.get(r.id)?.(r);
      worker.pending.delete(r.id);
    });
    child.stderr.resume();
    const drop = (why: string) => {
      this.workers = this.workers.filter((w) => w !== worker);
      for (const [id, resolve] of worker.pending) resolve(failed(id, why));
      worker.pending.clear();
    };
    child.on('exit', (code) => drop(`api cli exited (${code})`));
    child.on('error', (e) => drop(`api cli failed to start: ${e.message}`));
    this.workers.push(worker);
    return worker;
  }

  private pick(): Worker {
    const idle = this.workers.find((w) => w.pending.size === 0);
    if (idle) return idle;
    return this.workers.length < this.size ? this.start() : this.workers.reduce((a, b) => (b.pending.size < a.pending.size ? b : a));
  }

  private domain(name: string): Promise<unknown> {
    let p = this.domains.get(name);
    if (!p) {
      p = readFile(`${this.domainDir}/${name}.json`, 'utf8').then((t) => JSON.parse(t) as unknown);
      this.domains.set(name, p);
    }
    return p;
  }

  async run(domain: string, sources: string[]): Promise<ApiResult> {
    const id = String(this.n++);
    const descriptor = await this.domain(domain).catch((e: Error) => e);
    if (descriptor instanceof Error) return failed(id, `unknown domain ${domain}: ${descriptor.message}`);
    const worker = this.pick();
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        worker.pending.delete(id);
        resolve(failed(id, `api cli timed out after ${this.timeoutMs} ms`));
        worker.child.kill();
      }, this.timeoutMs);
      worker.pending.set(id, (r) => {
        clearTimeout(timer);
        resolve(r);
      });
      worker.child.stdin.write(JSON.stringify({ id, domain: descriptor, sources }) + '\n');
    });
  }

  close() {
    for (const w of this.workers) w.child.stdin.end();
    this.workers = [];
  }
}

const MOCK_DIR = 'idempotent-writes/evals';
const mockDomains: [string, string][] = [['/api/support-tickets', 'support'], ['/api/shipments', 'logistics'], ['/api/posts', 'blog']];

type Reference = { good: string; bad: Record<string, string> };

let targetedMock: Promise<Map<string, string>> | undefined;

// Targeted cases: the mock answers the first bad reference (the good subject with the opinion under test removed), so the mock baseline shows exactly the line's checks failing.
// A prompt shared by several cases (e.g. a targeted case whose prompt equals the naked one) is ambiguous and falls back to the generic mock.
const targetedAnswers = (): Promise<Map<string, string>> =>
  (targetedMock ??= (async () => {
    const seen = new Map<string, Set<string>>();
    const answers = new Map<string, string>();
    for (const home of ['idempotent-writes', 'api-design']) {
      const cases = JSON.parse(await readFile(`${home}/evals/cases.json`, 'utf8').catch(() => '[]')) as EvalCase[];
      const refs = JSON.parse(await readFile(`${home}/evals/references.json`, 'utf8').catch(() => '{}')) as Record<string, Reference>;
      for (const c of cases) {
        (seen.get(c.prompt) ?? seen.set(c.prompt, new Set()).get(c.prompt)!).add(c.targets.length ? c.id : '');
        const first = c.targets.length ? Object.values(refs[c.id]?.bad ?? {})[0] : undefined;
        if (first) answers.set(c.prompt, first);
      }
    }
    for (const [prompt, ids] of seen) if (ids.size > 1) answers.delete(prompt);
    return answers;
  })());

export async function mockApiAnswer(prompt: string): Promise<string> {
  const targeted = (await targetedAnswers()).get(prompt);
  if (targeted) return targeted;
  const domain = mockDomains.find(([route]) => prompt.includes(route))?.[1] ?? 'blog';
  const specified = prompt.includes('If-Match');
  const dir = specified ? `${MOCK_DIR}/subjects/${domain}` : `${MOCK_DIR}/mock/${domain}`;
  const files = (await readdir(dir).catch(() => [])).filter((f) => f.endsWith('.cs')).sort();
  if (!files.length) return '```csharp\n// the mock has no naive implementation for this domain\n```';
  const blocks = await Promise.all(files.map(async (f) => '```csharp\n' + (await readFile(`${dir}/${f}`, 'utf8')).trimEnd() + '\n```'));
  return blocks.join('\n\n');
}
