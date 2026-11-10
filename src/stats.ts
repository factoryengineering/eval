const choose = (n: number, k: number): number => {
  let result = 1;
  for (let i = 1; i <= k; i++) result = (result * (n - k + i)) / i;
  return result;
};

export function fisherOneSided(withPass: number, withN: number, withoutPass: number, withoutN: number): number {
  const passes = withPass + withoutPass;
  const total = withN + withoutN;
  let p = 0;
  for (let x = withPass; x <= Math.min(withN, passes); x++) {
    if (passes - x > withoutN) continue;
    p += (choose(withN, x) * choose(withoutN, passes - x)) / choose(total, passes);
  }
  return p;
}
