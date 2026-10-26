import { readFile } from 'node:fs/promises';
import { runEval } from './runner.js';
import { compare, logChange, readRuns, saveRun, table } from './store.js';
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
    if (flag('trials')) config.trials = Number(flag('trials'));
    const cases = JSON.parse(await readFile(config.cases, 'utf8')) as EvalCase[];
    const { summary, trials } = await runEval(config, skill, cases, undefined, (t) =>
      console.error(`${t.model} ${t.line} ${t.variant} ${t.caseId}#${t.trial} ${t.pass ? 'pass' : 'FAIL'}`));
    await saveRun(summary, trials);
    console.log(`\nrun ${summary.runId} skill ${summary.skillHash}\n${table(summary)}`);
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
    console.log('usage: cli <run [--models a,b] [--trials n] | change <note> | compare>');
}
