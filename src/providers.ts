import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';
import type { Completion, ModelSpec } from './types.js';

const run = promisify(execFile);
const estimate = (s: string) => Math.ceil(s.length / 4);

export async function complete(spec: ModelSpec, system: string, prompt: string): Promise<Completion> {
  switch (spec.provider) {
    case 'anthropic':
      return anthropic(spec.model, system, prompt);
    case 'devin-cli':
      return devinCli(spec.model, system, prompt);
    case 'mock':
      return mock(system, prompt);
  }
}

async function anthropic(model: string, system: string, prompt: string): Promise<Completion> {
  const key = process.env.ANTHROPIC_API_KEY;
  if (!key) throw new Error('ANTHROPIC_API_KEY is not set');
  const res = await fetch('https://api.anthropic.com/v1/messages', {
    method: 'POST',
    headers: { 'x-api-key': key, 'anthropic-version': '2023-06-01', 'content-type': 'application/json' },
    body: JSON.stringify({ model, max_tokens: 2048, system, messages: [{ role: 'user', content: prompt }] }),
  });
  if (!res.ok) throw new Error(`Anthropic ${res.status}: ${await res.text()}`);
  const body = (await res.json()) as {
    content: { type: string; text?: string }[];
    usage: { input_tokens: number; output_tokens: number };
  };
  const text = body.content.map((c) => c.text ?? '').join('');
  return { text, inputTokens: body.usage.input_tokens, outputTokens: body.usage.output_tokens, estimated: false };
}

// The Devin CLI prints plain text only, so token counts are estimates (~4 chars/token).
async function devinCli(model: string, system: string, prompt: string): Promise<Completion> {
  const full = `${system}\n\n---\n\n${prompt}\n\nDo not use any tools or read any files. Answer from this message alone.`;
  const dir = await mkdtemp(join(tmpdir(), 'ef-eval-'));
  const file = join(dir, 'prompt.txt');
  await writeFile(file, full);
  try {
    var { stdout } = await run('devin', ['-p', '--prompt-file', file, '--model', model, '--respect-workspace-trust', 'false'], {
      cwd: dir,
      maxBuffer: 10 * 1024 * 1024,
      timeout: 300_000,
    });
  } finally {
    await rm(dir, { recursive: true });
  }
  return { text: stdout, inputTokens: estimate(full), outputTokens: estimate(stdout), estimated: true };
}

// Deterministic stand-in: "obeys" the skill by echoing every backticked code span it contains.
function mock(system: string, prompt: string): Promise<Completion> {
  const spans = [...system.matchAll(/`([^`]+)`/g)].map((m) => m[1]);
  const text = `\`\`\`csharp\n${spans.join('\n')}\n\`\`\``;
  return Promise.resolve({
    text,
    inputTokens: estimate(system + prompt),
    outputTokens: estimate(text),
    estimated: true,
  });
}
