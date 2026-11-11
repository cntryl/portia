#!/usr/bin/env python3
"""Verify preserved compiler output snapshots and per-edit invalidation contracts."""

import hashlib
import json
import sys
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


EXPECTED_GENERATORS = {
    "unchanged": set(),
    "unrelated-body-edit": set(),
    "referenced-feature-replacement": set(),
    "call-site-whitespace": {
        "Cntryl.Portia.RegistrationCallInterceptorGenerator",
        "Cntryl.Portia.RequestHttpBindingGenerator",
    },
    "route-change": {
        "Cntryl.Portia.RegistrationCallInterceptorGenerator",
        "Cntryl.Portia.RequestHttpBindingGenerator",
    },
    "event-add": {
        "Cntryl.Portia.DomainEventCatalogGenerator",
        "Cntryl.Portia.JsonRootMarkerGenerator",
        "Cntryl.Portia.RegistrationCallInterceptorGenerator",
        "System.Text.Json.SourceGeneration.JsonSourceGenerator",
    },
    "event-remove": {
        "Cntryl.Portia.DomainEventCatalogGenerator",
        "Cntryl.Portia.JsonRootMarkerGenerator",
        "Cntryl.Portia.RegistrationCallInterceptorGenerator",
        "System.Text.Json.SourceGeneration.JsonSourceGenerator",
    },
    "event-rename": {
        "Cntryl.Portia.DomainEventCatalogGenerator",
        "Cntryl.Portia.JsonRootMarkerGenerator",
        "Cntryl.Portia.RegistrationCallInterceptorGenerator",
        "System.Text.Json.SourceGeneration.JsonSourceGenerator",
    },
}
EXPECTED_UNCHANGED_CROSS_VERSION_DIFFS = {
    "changed": {
        "Cntryl.Portia.DomainEventCatalogGenerator|DomainEventTypeCatalogGeneratedExtensions.g.cs",
        "Cntryl.Portia.ProjectorReactorEventDispatcherGenerator|BenchmarkProjector_918537A05B8B45E29CC302E11A55DBD0CF3D89350EBC7CF2B72F96F4FA5B0B0C.EventDispatcher.g.cs",
        "Cntryl.Portia.ProjectorReactorEventDispatcherGenerator|BenchmarkReactor_5720C775945ADA8EC393EE10C3600C7AD696B15ABDB8DFB31AF48E461E1683BA.EventDispatcher.g.cs",
        "Cntryl.Portia.RegistrationCallInterceptorGenerator|PortiaGeneratedRegistrationInterceptors.g.cs",
    },
    "baselineOnly": {
        "Cntryl.Portia.RequestHttpBindingGenerator|PortiaGeneratedHttpBinding.g.cs",
    },
    "currentOnlyCounts": {
        "Cntryl.Portia.DomainEventCatalogGenerator": 17,
        "Cntryl.Portia.RegistrationCallInterceptorGenerator": 19,
        "Cntryl.Portia.RequestHttpBindingGenerator": 2,
    },
}


def load_campaign(root: Path, label: str) -> dict:
    directory = root / label
    manifest = json.loads((directory / "manifest.json").read_text())
    require(manifest["framework"] == ".NET 11.0.0", f"{label}: unexpected runtime {manifest['framework']}")
    require(manifest["roslyn"] == "5.9.0.0", f"{label}: unexpected Roslyn {manifest['roslyn']}")
    for generator in manifest["generators"]:
        assembly_path = root / "assemblies" / label / f"{generator['name']}.dll"
        require(sha256(assembly_path.read_bytes()) == generator["hash"],
                f"{label}: generator assembly hash mismatch: {assembly_path}")

    for feature_index, (binary_hash, source_hash) in enumerate(
        zip(manifest["featureHashes"], manifest["featureSourceHashes"])
    ):
        source_path = root / label / "feature-inputs" / f"CompilerFeature{feature_index}" / "Feature.cs"
        binary_path = directory / "fixtures" / "combined" / f"CompilerFeature{feature_index}.dll"
        require(sha256(source_path.read_bytes()) == source_hash, f"{label}: feature source hash mismatch: {source_path}")
        require(sha256(binary_path.read_bytes()) == binary_hash, f"{label}: feature binary hash mismatch: {binary_path}")

    replacement_dir = directory / "inputs" / "referenced-feature-replacement"
    require(
        sha256((replacement_dir / "CompilerFeature0.Feature.cs").read_bytes())
        == manifest["replacementFeatureSourceHash"],
        f"{label}: replacement feature source hash mismatch",
    )
    require(
        sha256((replacement_dir / "CompilerFeature0.replacement.dll").read_bytes())
        == manifest["replacementFeatureHash"],
        f"{label}: replacement feature binary hash mismatch",
    )

    cases = {case["edit"]: case for case in manifest["cases"]}
    require(set(cases) == set(EXPECTED_GENERATORS), f"{label}: unexpected campaign cases {sorted(cases)}")
    for case_name, case in cases.items():
        require(case["outputCompiled"], f"{label}/{case_name}: generated consumer did not compile")
        for source in case["sourceInputs"]:
            path = directory / "inputs" / case_name / Path(source["path"]).name
            require(sha256(path.read_bytes()) == source["sha256"], f"{label}/{case_name}: source hash mismatch: {path}")
        contracts = case["contracts"]
        require(contracts["expectedEvents"] == contracts["generatedEvents"],
                f"{label}/{case_name}: generated event registrations differ from source identities")
        require(contracts["expectedJsonRoots"] == contracts["generatedJsonRoots"],
                f"{label}/{case_name}: generated JSON roots differ from source identities")
        for output in case["outputs"]:
            path = root / "generated-source-content" / output["content"]
            raw = path.read_bytes()
            require(sha256(raw) == output["hash"], f"{label}/{case_name}: generated source hash mismatch: {path}")
            require(len(raw) == output["bytes"], f"{label}/{case_name}: generated source length mismatch: {path}")
        check_invalidation(label, case)
    return {"manifest": manifest, "cases": cases}


def check_invalidation(label: str, case: dict) -> None:
    edit = case["edit"]
    delta = case["deltaFromUnchanged"]
    added, removed, changed = delta["added"], delta["removed"], delta["changed"]
    changed_generators = {key.split("|", 1)[0] for key in added + removed + changed}
    expected_generators = EXPECTED_GENERATORS[edit]
    require(changed_generators == expected_generators,
            f"{label}/{edit}: changed generator set {sorted(changed_generators)}, expected {sorted(expected_generators)}")

    if edit in {"unchanged", "unrelated-body-edit", "referenced-feature-replacement"}:
        require(not added and not removed and not changed, f"{label}/{edit}: expected no generated output invalidation")
    elif edit in {"call-site-whitespace", "route-change"}:
        require(not added and not removed and len(changed) == 2,
                f"{label}/{edit}: expected two changed interceptor outputs and no additions/removals")
        http_changes = [key for key in changed if key.startswith("Cntryl.Portia.RequestHttpBindingGenerator|")]
        require(len(http_changes) == 1 and (
            "Interceptors" in http_changes[0] or http_changes[0].endswith("|PortiaGeneratedHttpBinding.g.cs")
        ), f"{label}/{edit}: HTTP edit changed an unexpected output")
    elif edit == "event-add":
        require(len(added) == 10 and not removed and changed,
                f"{label}/{edit}: expected ten added JSON context outputs and related catalog changes")
    elif edit == "event-remove":
        require(not added and len(removed) == 1 and changed,
                f"{label}/{edit}: expected one removed JSON context output and related catalog changes")
    elif edit == "event-rename":
        require(len(added) == 1 and len(removed) == 1 and changed,
                f"{label}/{edit}: expected one replaced JSON context output and related catalog changes")


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: verify-compiler-output-identity.py EVIDENCE_DIRECTORY")
    root = Path(sys.argv[1]).resolve()
    baseline = load_campaign(root, "baseline")
    current = load_campaign(root, "current")
    before, after = baseline["manifest"], current["manifest"]
    require(before["harnessSourceHash"] == after["harnessSourceHash"], "baseline/current audit harness source differs")
    require(before["jsonGeneratorHash"] == after["jsonGeneratorHash"],
            "baseline/current runs did not use the same System.Text.Json source generator")
    json_generator = root / "assemblies" / "shared" / "System.Text.Json.SourceGeneration.dll"
    require(sha256(json_generator.read_bytes()) == before["jsonGeneratorHash"],
            "shared System.Text.Json source generator hash mismatch")
    require(before["sourceHash"] == after["sourceHash"], "baseline/current fixture source differs")
    require(before["featureSourceHashes"] == after["featureSourceHashes"], "baseline/current feature source differs")
    require(before["replacementFeatureSourceHash"] == after["replacementFeatureSourceHash"],
            "baseline/current replacement feature source differs")

    comparisons = []
    for edit in EXPECTED_GENERATORS:
        baseline_case = baseline["cases"][edit]
        current_case = current["cases"][edit]
        require(baseline_case["sourceHash"] == current_case["sourceHash"], f"{edit}: baseline/current source differs")
        before_contract = baseline_case["contracts"]
        after_contract = current_case["contracts"]
        for name in ("expectedEvents", "generatedEvents", "expectedJsonRoots", "generatedJsonRoots"):
            require(before_contract[name] == after_contract[name], f"{edit}: cross-version {name} identity differs")

        before_outputs = {f"{item['generator']}|{item['hintName']}": item["hash"] for item in baseline_case["outputs"]}
        after_outputs = {f"{item['generator']}|{item['hintName']}": item["hash"] for item in current_case["outputs"]}
        common = before_outputs.keys() & after_outputs.keys()
        same = sum(before_outputs[key] == after_outputs[key] for key in common)
        changed_keys = sorted(key for key in common if before_outputs[key] != after_outputs[key])
        baseline_only_keys = sorted(before_outputs.keys() - after_outputs.keys())
        current_only_keys = sorted(after_outputs.keys() - before_outputs.keys())
        if edit == "unchanged":
            current_only_counts: dict[str, int] = {}
            for key in current_only_keys:
                generator = key.split("|", 1)[0]
                current_only_counts[generator] = current_only_counts.get(generator, 0) + 1
            require(set(changed_keys) == EXPECTED_UNCHANGED_CROSS_VERSION_DIFFS["changed"],
                    f"unchanged: unexpected cross-version shared output hash changes: {changed_keys}")
            require(set(baseline_only_keys) == EXPECTED_UNCHANGED_CROSS_VERSION_DIFFS["baselineOnly"],
                    f"unchanged: unexpected baseline-only output keys: {baseline_only_keys}")
            require(current_only_counts == EXPECTED_UNCHANGED_CROSS_VERSION_DIFFS["currentOnlyCounts"],
                    f"unchanged: unexpected current-only generator output counts: {current_only_counts}")
            require(same == 2062, f"unchanged: expected 2,062 byte-identical output keys, found {same}")
        comparisons.append({
            "edit": edit,
            "sourceIdentityEqual": True,
            "eventIdentityEqual": True,
            "jsonRootIdentityEqual": True,
            "baselineOutputFiles": len(before_outputs),
            "currentOutputFiles": len(after_outputs),
            "sameKeySameHash": same,
            "sameKeyChangedHash": len(common) - same,
            "sameKeyChanged": [
                {"key": key, "baselineHash": before_outputs[key], "currentHash": after_outputs[key]}
                for key in changed_keys
            ],
            "baselineOnlyKeys": baseline_only_keys,
            "currentOnlyKeys": current_only_keys,
            "baselineDelta": baseline_case["deltaFromUnchanged"],
            "currentDelta": current_case["deltaFromUnchanged"],
        })

    summary = {
        "baselineSourceCommit": before["sourceCommit"],
        "currentSourceCommit": after["sourceCommit"],
        "harnessSourceHash": before["harnessSourceHash"],
        "framework": after["framework"],
        "roslyn": after["roslyn"],
        "identityScope": "event discriminator/type/version registrations and JSON root/context pairs; raw generated source hashes are inventoried per generator and hint",
        "byteEqualityAcrossVersionsClaimed": False,
        "cases": comparisons,
    }
    (root / "cross-version.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"Verified {len(comparisons)} edits across baseline {before['sourceCommit']} and current {after['sourceCommit']}.")
    print("Event registration and JSON root identities match source and each other across versions.")
    print("Generated output content hashes, raw source inputs, and expected invalidation sets verified.")


if __name__ == "__main__":
    main()
