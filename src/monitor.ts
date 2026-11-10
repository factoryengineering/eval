const tail = (n: number, p: number, k: number): number => {
  let sum = 0;
  let term = (1 - p) ** n;
  for (let i = 0; i <= n; i++) {
    if (i >= k) sum += term;
    term *= ((n - i) / (i + 1)) * (p / (1 - p));
  }
  return sum;
};

export interface MonitorSpec {
  noise: number;
  alpha: number;
  effect: number;
  power: number;
}

export const DEFAULT_MONITOR: MonitorSpec = { noise: 0.05, alpha: 0.05, effect: 0.3, power: 0.8 };

export const criticalFailures = (n: number, spec: MonitorSpec): number | undefined => {
  for (let k = 1; k <= n; k++) if (tail(n, spec.noise, k) <= spec.alpha) return k;
  return undefined;
};

export const detectionPower = (n: number, spec: MonitorSpec): number => {
  const k = criticalFailures(n, spec);
  return k === undefined ? 0 : tail(n, spec.effect, k);
};

export const trialsNeeded = (spec: MonitorSpec, max = 200): number => {
  for (let n = 1; n <= max; n++) if (detectionPower(n, spec) >= spec.power) return n;
  throw new Error('no sample size within limit reaches the requested power');
};

export const isSignal = (failures: number, n: number, spec: MonitorSpec): boolean => {
  const k = criticalFailures(n, spec);
  return k !== undefined && failures >= k;
};
