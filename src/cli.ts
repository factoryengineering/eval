import { appendFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { runEval } from './runner.js';
import { compare, logChange, readRuns, saveRun, table } from './store.js';
import { clauseVerdicts, descriptionOf, rates, runTrigger } from './trigger.js';
import type { TriggerSpec } from './trigger.js';
import type { Config, EvalCase } from './types.js';

const [cmd, ...args] = process.argv.slice(2);
const flag = (name: string) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
};

const config = JSON.parse(await readFile('eval.config.json', 'utf8')) as Config;
const skill = await readFile(config.skill, 'utf8');

switch (cmd) {
  case 'run': {
    const only = flag('models')?.split(',');
    if (only) config.models = config.models.filter((m) => only.includes(m.id));
    if (flag('lines')) config.lines = flag('lines')!.split(',');
    if (flag('trials')) config.trials = Number(flag('trials'));
    const cases = JSON.parse(await readFile(config.cases, 'utf8')) as EvalCase[];
    const { summary, trials } = await runEval(config, skill, cases, undefined, (t) =>
      console.error(`${t.model} ${t.line} ${t.variant} ${t.caseId}#${t.trial} ${t.pass ? 'pass' : 'FAIL'}`));
    await saveRun(summary, trials);
    console.log(`\nrun ${summary.runId} skill ${summary.skillHash}\n${table(summary)}`);
    break;
  }
  case 'trigger': {
    const only = flag('models')?.split(',');
    if (only) config.models = config.models.filter((m) => only.includes(m.id));
    if (flag('trials')) config.trials = Number(flag('trials'));
    const spec = JSON.parse(await readFile('evals/trigger.json', 'utf8')) as TriggerSpec;
    const { trials, variants } = await runTrigger(spec, config.models, config.trials, descriptionOf(skill), undefined, 6, (t) =>
      console.error(`${t.model} ${t.variant} ${t.caseId}#${t.trial} ${t.pass ? 'pass' : 'FAIL'}`));
    const cells = rates(trials, spec);
    const verdicts = clauseVerdicts(cells, spec, config.minDelta);
    const at = new Date().toISOString();
    const runId = at.replace(/[:.]/g, '-');
    await mkdir('runs', { recursive: true });
    await mkdir('logs', { recursive: true });
    await writeFile(`runs/trigger-${runId}.json`, JSON.stringify({ runId, at, variants, cells, verdicts, trials }, null, 2));
    await appendFile('logs/trigger.jsonl', JSON.stringify({ runId, at, trials: config.trials, variants, cells, verdicts }) + '\n');
    console.log(`\ntrigger run ${runId}`);
    for (const v of variants) console.log(`${v.id.padEnd(14)} ${JSON.stringify(v.description)}`);
    console.log('');
    for (const c of cells.filter((x) => ['all', 'positives', 'negatives'].includes(x.scope)))
      console.log(`${c.model.padEnd(12)} ${c.variant.padEnd(14)} ${c.scope.padEnd(10)} ${c.rate.toFixed(2)} (n=${c.n})`);
    console.log('');
    for (const v of verdicts)
      console.log(`${v.model.padEnd(12)} ${v.clause.padEnd(11)} without ${v.withoutRate.toFixed(2)}  with ${v.withRate.toFixed(2)}  delta ${v.delta.toFixed(2)}  ${v.verdict}`);
    break;
  }
  case 'change': {
    const note = args.join(' ');
    if (!note) throw new Error('usage: change <note describing the skill edit>');
    console.log(`logged change for skill ${await logChange(note, skill)}`);
    break;
  }
  case 'compare': {
    const runs = await readRuns();
    if (runs.length < 2) throw new Error('need at least two runs to compare');
    console.log(compare(runs.at(-2)!, runs.at(-1)!));
    break;
  }
  default:
    console.log('usage: cli <run [--models a,b] [--lines x,y] [--trials n] | trigger [--models a,b] [--trials n] | change <note> | compare>');
}
