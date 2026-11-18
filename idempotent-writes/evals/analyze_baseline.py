#!/usr/bin/env python3
"""Baseline analysis for the api homes: python3 idempotent-writes/evals/analyze_baseline.py <baseline-run.json> [--scenarios]
Pools by specificity level (naked 1, mild 2, specified 3) and reports per domain; signal = one-sided binomial vs 0.05 noise, alpha 0.05 (src/monitor.ts)."""
import json, sys, math, collections

NOISE, ALPHA = 0.05, 0.05
LEVELS = {"naked": 1, "mild": 2, "specified": 3}
MODELS = ["haiku-5.5", "sonnet-5.5", "gpt-6-luna", "swe-2", "swe-2-flash"]


def tail(n, p, k):
    return sum(math.comb(n, i) * p**i * (1 - p) ** (n - i) for i in range(k, n + 1))


def crit(n):
    return next((k for k in range(1, n + 1) if tail(n, NOISE, k) <= ALPHA), None)


def signal(f, n):
    k = crit(n)
    return k is not None and f >= k


def bucket(f):
    return f.split(":")[0]


def scen(f):
    p = f.split(": ")
    return p[1] if p[0].startswith("api scenario") and len(p) > 2 else None


def info(t):
    dom, lvl = t["caseId"].rsplit("-", 1)
    fs = t["failures"]
    compiled = not any(f.startswith(("api compile", "api host", "api error")) for f in fs)
    return dict(m=t["model"], dom=dom, lvl=LEVELS[lvl], compiled=compiled, create_failed=bool(t.get("bootstrapNote")), buckets={bucket(f) for f in fs}, scen={s for s in map(scen, fs) if s}, kinds=[bucket(f) for f in fs if f.startswith(("api compile", "api host", "api error"))], t=t)


def cell(f, n):
    return f"{f}/{n}{'*' if signal(f, n) else ''}"


def main(path, per_scenario=False):
    run = json.load(open(path))
    run["trials"] = [t for t in run["trials"] if t["caseId"].rsplit("-", 1)[-1] in LEVELS]  # targeted cases (iteration A12) are analysed per line, not per level
    errors = [t for t in run["trials"] if t["failures"] and t["failures"][0].startswith("provider error")]
    trials = [info(t) for t in run["trials"] if t not in errors]
    models = [m for m in MODELS if any(t["m"] == m for t in trials)]
    out = []
    n21, n14, n7 = 21, 14, 7
    out.append(f"Signal threshold (one-sided binomial vs {NOISE} noise, alpha {ALPHA}): n=21 -> {crit(21)} failing trials; n=14 -> {crit(14)}; n=7 -> {crit(7)}; n=3 -> {crit(3)}. `*` marks a signal. A trial counts once per bucket.")
    ec = collections.Counter((t["model"], LEVELS[t["caseId"].rsplit("-", 1)[1]]) for t in errors)
    out.append("")
    out.append("Provider errors (excluded from every count below; the model call failed after 3 attempts, not a model answer): " + ("; ".join(f"{m} level {l}: {c}" for (m, l), c in sorted(ec.items())) or "none") + ".")
    out.append("")
    out.append("Per-model summary (n = trials per cell; compile = compiled and hosted; create-failed = `create-succeeds` failed among compiled trials; mean failing = mean number of failing buckets among compiled trials):")
    out.append("")
    out.append("| level | model | n | compile | create-failed | mean failing buckets | all checks pass |")
    out.append("|---|---|---|---|---|---|---|")
    for lvl in (1, 2, 3):
        for m in models:
            ts = [t for t in trials if t["m"] == m and t["lvl"] == lvl]
            c = [t for t in ts if t["compiled"]]
            cf = sum(t["create_failed"] for t in c)
            mean = sum(len(t["buckets"]) for t in c) / len(c) if c else float("nan")
            out.append(f"| {lvl} | {m} | {len(ts)} | {len(c)}/{len(ts)} | {cf}/{len(c)} | {mean:.1f} | {sum(t['t']['pass'] for t in ts)}/{len(ts)} |")
    out.append("")
    all_buckets = sorted({b for t in trials for b in t["buckets"]})
    for lvl in (1, 2, 3):
        for title, filt in (("all trials", lambda t: True), ("compiled trials only", lambda t: t["compiled"]), ("compiled and create passed (independent of the create bootstrap)", lambda t: t["compiled"] and not t["create_failed"])):
            sub = {m: [t for t in trials if t["m"] == m and t["lvl"] == lvl and filt(t)] for m in models}
            rows = []
            for b in all_buckets:
                cnt = {m: sum(b in t["buckets"] for t in sub[m]) for m in models}
                if any(cnt.values()):
                    rows.append((b, cnt))
            out.append(f"Level {lvl}, {title} (n per model: {', '.join(f'{m} {len(sub[m])}' for m in models)}); thresholds apply to the stated n only:")
            out.append("")
            out.append("| check bucket | " + " | ".join(models) + " |")
            out.append("|---|" + "---|" * len(models))
            for b, cnt in sorted(rows, key=lambda r: -max(r[1].values())):
                out.append(f"| {b} | " + " | ".join(cell(cnt[m], len(sub[m])) for m in models) + " |")
            if not rows:
                out.append("| (no failures) |" + " |" * len(models))
            out.append("")
    # checks that never fail anywhere (compiled trials)
    out.append("Never failing buckets (any level, any model, over all trials): " + (", ".join(f"`{b}`" for b in sorted(set(run_buckets(path)) - set(all_buckets))) or "none") + ".")
    out.append("")
    # domain concentration for buckets failing on >= 3 compiled trials of some model-level
    out.append("Per-domain failure counts (compiled trials, per level; format blog/support/logistics, n per domain cell = 7 per model, 35 over five models):")
    out.append("")
    out.append("| level | bucket | " + " | ".join(models) + " |")
    out.append("|---|---|" + "---|" * len(models))
    for lvl in (1, 2, 3):
        for b in all_buckets:
            row = []
            tot = 0
            for m in models:
                c = []
                for d in ("blog", "support", "logistics"):
                    ts = [t for t in trials if t["m"] == m and t["lvl"] == lvl and t["dom"] == d and t["compiled"]]
                    c.append(sum(b in t["buckets"] for t in ts))
                tot += sum(c)
                row.append("/".join(map(str, c)))
            if tot >= 3 and not b.startswith("api compile"):
                out.append(f"| {lvl} | {b} | " + " | ".join(row) + " |")
    out.append("")
    if per_scenario:
        out.append("Per-scenario failures among compiled trials with create passed (any model; count of trials, level 3 and levels 1-2 separately):")
        out.append("")
        for lv, name in (((3,), "level 3"), ((1, 2), "levels 1-2")):
            ctr = collections.Counter()
            tot = 0
            for t in trials:
                if t["lvl"] in lv and t["compiled"] and not t["create_failed"]:
                    tot += 1
                    ctr.update(t["scen"])
            out.append(f"{name} (n={tot}): " + "; ".join(f"{k} {v}" for k, v in ctr.most_common()))
            out.append("")
    # compile error classes
    out.append("Compile failure classes (first error id, all levels): " + "; ".join(f"{k} {v}" for k, v in collections.Counter(
        (t["t"]["failures"][0].split("Error ")[1].split(":")[0] if "Error " in t["t"]["failures"][0] else t["t"]["failures"][0][:40]) for t in trials if not t["compiled"]).most_common()))
    return "\n".join(out)


def run_buckets(path):
    # buckets that could fail in this home: taken from the run's own monitor log line when present
    import glob, os
    home = os.path.dirname(os.path.dirname(path)) if os.path.basename(os.path.dirname(path)) == "runs" else "."
    rid = json.load(open(path))["runId"]
    for line in open(f"{home}/logs/baseline.jsonl"):
        d = json.loads(line)
        if d["runId"] == rid:
            return sorted({r["check"] for r in d["monitor"]})
    return []


if __name__ == "__main__":
    if "--matrix" not in sys.argv:
        print(main(sys.argv[1], "--scenarios" in sys.argv))


def matrix(path):
    """Compact signal matrix: per bucket, which models signal at each level (H haiku, S sonnet, L luna, W swe-2, F swe-2-flash).
    L3c = level 3 restricted to compiled trials whose create passed (n varies; threshold for that n)."""
    run = json.load(open(path))
    trials = [info(t) for t in run["trials"] if not (t["failures"] and t["failures"][0].startswith("provider error"))]
    ini = {"haiku-5.5": "H", "sonnet-5.5": "S", "gpt-6-luna": "L", "swe-2": "W", "swe-2-flash": "F"}
    buckets = sorted({b for t in trials for b in t["buckets"]} | set(run_buckets(path)))
    rows = ["| check bucket | L1 | L2 | L3 | L3c | failing trials L1/L2/L3 (all models) |", "|---|---|---|---|---|---|"]
    for b in buckets:
        cols = []
        for lvl, cond in ((1, False), (2, False), (3, False), (3, True)):
            s = ""
            for m in MODELS:
                ts = [t for t in trials if t["m"] == m and t["lvl"] == lvl and (not cond or (t["compiled"] and not t["create_failed"]))]
                f = sum(b in t["buckets"] for t in ts)
                if ts and signal(f, len(ts)):
                    s += ini[m]
            cols.append(s or "-")
        tot = [sum(b in t["buckets"] for t in trials if t["lvl"] == l) for l in (1, 2, 3)]
        rows.append(f"| {b} | " + " | ".join(cols) + f" | {tot[0]}/{tot[1]}/{tot[2]} |")
    return "\n".join(rows)


if __name__ == "__main__" and "--matrix" in sys.argv:
    print(matrix(sys.argv[1]))
