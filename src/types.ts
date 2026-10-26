export interface ModelSpec {
  id: string;
  provider: 'anthropic' | 'devin-cli' | 'mock';
  model: string;
}

export interface Config {
  skill: string;
  ids: string;
  cases: string;
  trials: number;
  minDelta: number;
  lines?: string[];
  models: ModelSpec[];
}

export interface SkillLine {
  id: string;
  text: string;
}

export interface ExecSpec {
  args?: { string?: string; int?: number };
  sqlMust?: string[];
  sqlMustNot?: string[];
  minCommands?: number;
  noMethodSyntax?: string[];
  judgeNames?: boolean;
  separateQuery?: boolean;
}

export interface EvalCase {
  id: string;
  prompt: string;
  targets: string[];
  guard?: boolean;
  exec?: ExecSpec;
  must: string[];
  mustNot?: string[];
}

export interface Completion {
  text: string;
  inputTokens: number;
  outputTokens: number;
  estimated: boolean;
}

export type Variant = 'with' | 'without';

export interface Trial {
  model: string;
  line: string;
  variant: Variant;
  caseId: string;
  trial: number;
  pass: boolean;
  failures: string[];
  inputTokens: number;
  outputTokens: number;
  estimated: boolean;
  output: string;
}

export interface LineResult {
  model: string;
  line: string;
  withRate: number;
  withoutRate: number;
  delta: number;
  verdict: 'load-bearing' | 'not-load-bearing';
}

export interface RunSummary {
  runId: string;
  at: string;
  skillHash: string;
  trials: number;
  lines: LineResult[];
  guards?: { model: string; caseId: string; rate: number; n: number }[];
  tokens: Record<string, { input: number; output: number; estimated: boolean; calls: number }>;
}
