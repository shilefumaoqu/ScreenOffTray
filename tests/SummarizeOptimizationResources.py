"""Offline analysis only; never opens a process, configuration or device."""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("directory", type=Path)
p.add_argument("--processors", type=int, required=True)
p.add_argument("--allow-incomplete", action="store_true")
a = p.parse_args()
status = json.loads((a.directory / "status.json").read_text(encoding="utf-8-sig"))
complete = (status.get("Status") == "completed"
            and status.get("Result", {}).get("Status") == "completed"
            and status.get("Result", {}).get("Seconds", 0) >= status["Seconds"]
            and not status.get("ForcedCleanup"))
if not complete and not a.allow_incomplete:
    raise SystemExit("Not a completed acceptance run; use --allow-incomplete for a labelled preview")
with (a.directory / "resources.csv").open(encoding="utf-8-sig", newline="") as f:
    rows = list(csv.DictReader(f))
for row in rows:
    for key in ("seconds", "cpuMs"):
        row[key] = float(row[key])
    for key in ("privateResident", "workingSet", "privateCommit", "handles", "threads",
                "gdi", "user", "readBytes", "writeBytes", "audioReads", "displayReads",
                "displayFailures", "sources", "managed", "gc0", "gc1", "gc2"):
        row[key] = int(row[key])
backend = [r for r in rows if r["role"] == "backend"]
ui = [r for r in rows if r["role"] == "settings"]
valid = [r for r in backend if r["phase"] == "background" and r["seconds"] >= 300]

def stats(values):
    v = sorted(values)
    if not v:
        return None
    return dict(min=v[0], p50=statistics.median(v),
                p95=v[max(0, math.ceil(len(v)*.95)-1)], max=v[-1])

frames = []
for row in rows:
    if row["role"] == "backend":
        frames.append({key: row[key] for key in ("seconds", "privateResident", "workingSet", "privateCommit", "gdi", "user", "handles", "threads")})
    elif frames:
        for key in ("privateResident", "workingSet", "privateCommit", "gdi", "user", "handles", "threads"):
            frames[-1][key] += row[key]
span = backend[-1]["seconds"] - backend[0]["seconds"] if len(backend)>1 else 0
buckets = []
for start in range(0, status["Seconds"], 1200):
    points = [r for r in valid if start <= r["seconds"] < start+1200]
    if points:
        buckets.append(dict(fromMinutes=start/60, toMinutes=(start+1200)/60,
                            samples=len(points),
                            medianResident=statistics.median(r["privateResident"] for r in points),
                            medianCommit=statistics.median(r["privateCommit"] for r in points),
                            medianManaged=statistics.median(r["managed"] for r in points)))
summary = dict(
    AcceptanceCompleted=complete, Scope="isolated combination, not full installed product",
    Status=status["Status"], Seconds=status.get("Result", {}).get("Seconds"),
    BackendSamples=len(backend), ValidBackgroundSamples=len(valid), UiSamples=len(ui),
    BackgroundUnder20MB=(bool(valid) and all(r["privateResident"] < 20_000_000 for r in valid)),
    BackendAll={k:stats(r[k] for r in backend) for k in ("privateResident","workingSet","privateCommit","handles","threads","gdi","user","managed")},
    BackendValid={k:stats(r[k] for r in valid) for k in ("privateResident","workingSet","privateCommit","handles","threads","gdi","user","managed")},
    UiObserved={k:stats(r[k] for r in ui) for k in ("privateResident","workingSet","privateCommit","handles","threads","gdi","user")},
    ProductObservedSum={k:stats(r[k] for r in frames) for k in ("privateResident","workingSet","privateCommit","handles","threads","gdi","user")},
    BackendAverageWholeMachineCpuPercent=(100*(backend[-1]["cpuMs"]-backend[0]["cpuMs"])/(span*1000*a.processors) if span else None),
    LogicalProcessorsForCpu=a.processors,
    BackendIoDelta={key:backend[-1][key]-backend[0][key] for key in ("readBytes","writeBytes")} if backend else None,
    LastQueryCounters={k:backend[-1][k] for k in ("audioReads","displayReads","displayFailures","gc0","gc1","gc2")} if backend else None,
    CommitBuckets=buckets,
    Caveats=["I/O includes probe status/event files and IPC, not physical disk I/O or production logging rate",
             "DisplayReads in the long-run binary counts calls started; failures and the separate first-error diagnostic must be read together",
             "UI observed samples are not guaranteed to capture an instantaneous peak",
             "Product memory/handles are sums of sequentially read processes in one sampling pass; commit is never added to resident",
             "CPU summary is exact for the sampled backend span; final CPU of exited UI is not persisted in CSV, so no exact full-product CPU total is asserted",
             "Successful display query, real input hooks/guardian and installed entry are outside this run"])
(a.directory / "resource-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({k:summary[k] for k in ("AcceptanceCompleted","ValidBackgroundSamples","BackgroundUnder20MB","BackendValid","BackendAverageWholeMachineCpuPercent")}, ensure_ascii=False, indent=2))
