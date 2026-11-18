"""Probe mutants through ApiConformance.Cli: python3 idempotent-writes/evals/probe_mutants.py [--fill] <mutant names...>
Prints the failing checks per layer; with --fill writes them into mutants.json as the expected sets (review them before committing)."""
import json
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, "idempotent-writes/evals")
import generate as g

FIELDS = {"scenario": "expectedFailures", "static": "staticExpectedFailures", "lint": "lintExpectedFailures", "spec": "specExpectedFailures"}


def probe(mutant):
    domain = mutant.get("domain", "blog")
    files = g.mutate(g.read_subject(domain), mutant)
    request = json.dumps({"id": mutant["name"], "domain": g.descriptor(domain), "sources": list(files.values())})
    out = subprocess.run(["dotnet", g.CLI], input=request + "\n", capture_output=True, text=True, check=True).stdout
    result = json.loads(out)
    if not result["compile"]["ok"]:
        raise SystemExit(f"{mutant['name']}: does not compile: {result['compile']['errors'][:3]}")
    failing = {layer: sorted(c["id"] for c in result["checks"] if c["layer"] == layer and not c["passed"] and not c["advisory"]) for layer in FIELDS}
    advisory = sorted(c["id"] for c in result["checks"] if c["advisory"] and not c["passed"])
    return failing, advisory


def main():
    fill = "--fill" in sys.argv
    names = [a for a in sys.argv[1:] if not a.startswith("--")]
    mutants = json.load(open(f"{g.EVALS}/mutants.json"))
    chosen = [m for m in mutants if m["name"] in names]
    with ThreadPoolExecutor(4) as pool:
        results = list(pool.map(probe, chosen))
    for m, (failing, advisory) in zip(chosen, results):
        print(f"== {m['name']} ({m.get('domain', 'blog')})" + (f" advisory {advisory}" if advisory else ""))
        for layer, ids in failing.items():
            print(f"  {layer}: {ids}")
        if fill:
            for layer, field in FIELDS.items():
                m.pop(field, None)
            for layer, field in FIELDS.items():
                m[field] = failing[layer]
            for field in list(FIELDS.values())[1:]:
                if not m[field]:
                    del m[field]
    if fill:
        with open(f"{g.EVALS}/mutants.json", "w") as f:
            json.dump(mutants, f, indent=2, ensure_ascii=False)
            f.write("\n")


if __name__ == "__main__":
    main()
