#!/usr/bin/env python3
"""Summarize accepted hosted Fitz runs using one observation per process pair."""

import argparse
import hashlib
import json
import math
import re
import statistics
from datetime import datetime
from pathlib import Path


MATRIX = {(4, 1), (4, 8), (128, 1), (128, 8)}
PHASES = ("Normal", "SharedOutage", "Recovery")
ENVIRONMENT = (
    "architecture", "broker", "dependencies", "endpoint", "machine", "os",
    "processorCount", "runtime", "sdk", "statusProbeIntervalMs", "storage",
)
MARGINS = {"throughput", "tailLatency", "recoveryTime"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def utc(value):
    time = datetime.fromisoformat(value)
    require(time.utcoffset() is not None, f"Time requires a timezone: {value}")
    return time


def positive(value):
    return type(value) in (int, float) and math.isfinite(value) and value > 0


def tail(samples, percentile):
    return sorted(samples)[math.ceil(len(samples) * percentile / 100) - 1]


def process_metrics(artifact):
    result = {}
    realms = set()
    rows = set()
    for row in artifact["rows"]:
        cell = (row["History"], row["Workloads"])
        realm = row["Realm"]
        require(cell in MATRIX and cell not in rows, f"Unexpected or repeated cell {cell}")
        require(realm and realm not in realms, f"Missing or repeated Fitz realm {realm!r}")
        rows.add(cell)
        realms.add(realm)
        for phase in PHASES:
            sample = row[phase]
            label = f"{cell} {phase}"
            elapsed = sample["ElapsedMs"]
            commits = sample["commits"]
            status = [entry["LatencyMs"] for entry in sample["status"]]
            expected_commits = {"Normal": 20, "SharedOutage": 0, "Recovery": 1}[phase] * cell[1]
            expected_retries = 3 * cell[1] if phase == "SharedOutage" else 0
            require(positive(elapsed), f"{label} has invalid phase duration")
            require(len(commits) == expected_commits and all(map(positive, commits)),
                    f"{label} has invalid commit samples")
            require(sample["retryWakeups"] == expected_retries, f"{label} has an unexpected retry count")
            require(status and all(map(positive, status)), f"{label} has invalid or missing status samples")
            for percentile in (95, 99):
                result[cell, phase, f"status_p{percentile}_ms", "tailLatency"] = tail(status, percentile)
                if phase == "Normal":
                    result[cell, phase, f"publish_to_commit_p{percentile}_ms", "tailLatency"] = tail(commits, percentile)
            if commits:
                result[cell, phase, "commits_per_second", "throughput"] = len(commits) * 1000 / elapsed
            if phase == "Recovery":
                result[cell, phase, "elapsed_ms", "recoveryTime"] = elapsed
    require(rows == MATRIX, "Artifact lacks the full history and workload matrix")
    return result, realms


def read_artifact(entry, root, revision, declared):
    path = (root / entry["artifact"]).resolve()
    started = utc(entry["startedUtc"])
    require(started >= declared, f"{path}: run started before policy declaration")
    data = path.read_bytes()
    artifact = json.loads(data)
    require(artifact["complete"] is True and artifact["smoke"] is False,
            f"{path}: incomplete or smoke artifact")
    require(artifact["revision"] == revision, f"{path}: unexpected source revision")
    finished = utc(artifact["generatedUtc"])
    require(finished > started, f"{path}: invalid process time range")
    metrics, realms = process_metrics(artifact)
    return {
        "metrics": metrics,
        "realms": realms,
        "environment": {field: artifact[field] for field in ENVIRONMENT},
        "started": started,
        "finished": finished,
        "receipt": {"artifact": entry["artifact"], "sha256": hashlib.sha256(data).hexdigest(),
                    "revision": revision, "startedUtc": entry["startedUtc"],
                    "finishedUtc": artifact["generatedUtc"]},
    }


def median_interval(values):
    """Narrowest order-statistic interval with at least 95% sign-test coverage."""
    ordered = sorted(values)
    count = len(ordered)
    # Twice the lower binomial tail has numerator 2 * sum(C(n, i)).
    tail_count = 0
    limits = None
    for rank in range((count + 1) // 2):
        tail_count += math.comb(count, rank)
        if 40 * tail_count > 2 ** count:
            break
        limits = (ordered[rank], ordered[-rank - 1], 1 - 2 * tail_count / 2 ** count)
    require(limits is not None, "At least six process pairs are required for a 95% interval")
    return limits


def compare(manifest_path):
    manifest = json.loads(manifest_path.read_bytes())
    policy = manifest["policy"]
    declared = utc(policy["declaredUtc"])
    margins = policy["marginsPercent"]
    require(set(margins) == MARGINS, "Declare throughput, tailLatency and recoveryTime margins")
    for metric, margin in margins.items():
        require(type(margin) in (int, float) and math.isfinite(margin) and 0 <= margin,
                f"Invalid percent margin for {metric}")
    require(policy["hostAcceptance"], "Host acceptance criteria are required")
    for field in ("baselineRevision", "candidateRevision", "harnessRevision"):
        require(re.fullmatch(r"[0-9a-f]{40}", policy[field]), f"{field} requires a full Git SHA")
    for field in ("baselineOverlaySha256", "candidateOverlaySha256"):
        require(re.fullmatch(r"[0-9a-f]{64}", policy[field]), f"{field} requires an overlay SHA-256")
    pairs = manifest["pairs"]
    orders = [pair["first"] for pair in pairs]
    require(len(pairs) >= 6 and orders.count("baseline") == orders.count("candidate") == len(pairs) / 2,
            "Provide at least six pairs, equally split between the two process orders")
    revisions = {"baseline": policy["baselineRevision"], "candidate": policy["candidateRevision"]}
    samples = {}
    environment = None
    realms = set()
    receipts = []
    previous_finish = declared

    for index, pair in enumerate(pairs, start=1):
        require(pair["hostAccepted"] is True and pair["hostEvidence"],
                f"Pair {index}: record accepted host evidence")
        runs = {role: read_artifact(pair[role], manifest_path.parent, revision, declared)
                for role, revision in revisions.items()}
        first = pair["first"]
        second = "candidate" if first == "baseline" else "baseline"
        for role in (first, second):
            run = runs[role]
            require(run["started"] >= previous_finish, f"Pair {index}: incorrect or overlapping process order")
            previous_finish = run["finished"]
            if environment is None:
                environment = run["environment"]
            require(run["environment"] == environment, f"Pair {index}: artifact environment mismatch")
            require(not realms.intersection(run["realms"]), f"Pair {index}: repeated Fitz realm")
            realms.update(run["realms"])
        for key, baseline in runs["baseline"]["metrics"].items():
            candidate = runs["candidate"]["metrics"][key]
            loss = 1 - candidate / baseline if key[-1] == "throughput" else candidate / baseline - 1
            samples.setdefault(key, []).append((baseline, candidate, 100 * loss))
        receipts.append({"pair": index, "first": first, "hostEvidence": pair["hostEvidence"],
                         **{role: run["receipt"] for role, run in runs.items()}})

    rows = []
    for (cell, phase, metric, group), paired in sorted(samples.items()):
        baseline, candidate, losses = zip(*paired)
        lower, upper, coverage = median_interval(losses)
        margin = margins[group]
        verdict = ("within_margin" if upper <= margin else "exceeds_margin" if lower > margin
                   else "inconclusive")
        rows.append({"history": cell[0], "workloads": cell[1], "phase": phase, "metric": metric,
                     "marginPercent": margin, "baselineByPair": baseline, "candidateByPair": candidate,
                     "pairedDeteriorationPercent": losses,
                     "medianPercent": statistics.median(losses), "intervalPercent": [lower, upper],
                     "intervalCoverage": coverage, "verdict": verdict})
    return {"policy": policy, "environment": environment, "pairs": receipts, "results": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(compare(args.manifest), indent=2, allow_nan=False))
    except (OSError, ValueError, KeyError, TypeError, OverflowError) as exc:
        parser.exit(2, f"Comparison rejected: {exc}\n")


if __name__ == "__main__":
    main()
