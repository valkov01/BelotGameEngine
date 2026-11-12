#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

mode="${1:-}"
output="artifacts/AI-01/batches"
runner="tools/NeuralTrainer/bin/Release/net10.0/NeuralTrainer.dll"

if [[ ! -f "$runner" ]]; then
    printf 'Release runner not found; build NeuralTrainer first: %s\n' "$runner" >&2
    exit 1
fi

mkdir -p "$output"

run_batch() {
    local name="$1"
    local seed="$2"
    local pairs="$3"
    local data="$output/$name"
    local report="$data.arena.json"
    local log="$data.log"

    if [[ -e "$report" ]]; then
        python3 - "$report" "$seed" "$pairs" <<'PY'
import json
import sys

path, seed, pairs = sys.argv[1:]
with open(path, encoding="utf-8") as source:
    report = json.load(source)
if report["Player"] != "heuristic-endgame" or report["Opponent"] != "master":
    raise SystemExit(f"Refusing to reuse {path}: profiles do not match AI-01.")
if report["Seed"] != int(seed) or report["Pairs"] != int(pairs):
    raise SystemExit(f"Refusing to reuse {path}: seed or pair count does not match AI-01.")
if len(report["Result"]["PairScores"]) != int(pairs) or len(report["Result"]["PairPoints"]) != int(pairs):
    raise SystemExit(f"Refusing to reuse {path}: pair results are incomplete.")
print(f"already complete: {path}")
PY
        python3 tools/NeuralTrainer/ai01-summarize.py
        return
    fi

    python3 - "$runner" "$data" "$seed" "$pairs" <<'PY' | tee -a "$log"
import json
import resource
import subprocess
import sys
import time

runner, data, seed, pairs = sys.argv[1:]
command = [
    "dotnet", runner, "arena",
    "--player", "heuristic-endgame",
    "--opponent", "master",
    "--pairs", pairs,
    "--threads", "1",
    "--seed", seed,
    "--data", data,
]
before = resource.getrusage(resource.RUSAGE_CHILDREN)
started = time.monotonic()
process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
for line in process.stdout:
    print(line, end="", flush=True)
return_code = process.wait()
elapsed = time.monotonic() - started
after = resource.getrusage(resource.RUSAGE_CHILDREN)
user = after.ru_utime - before.ru_utime
system = after.ru_stime - before.ru_stime
resources = {
    "seed": int(seed),
    "pairs": int(pairs),
    "wall_seconds": elapsed,
    "user_cpu_seconds": user,
    "system_cpu_seconds": system,
    "cpu_percent_of_one_core": (user + system) * 100 / elapsed if elapsed else 0,
    "max_rss_mb": after.ru_maxrss / 1024,
    "return_code": return_code,
}
with open(data + ".resources.json", "w", encoding="utf-8") as destination:
    json.dump(resources, destination, indent=2)
    destination.write("\n")
print("resource use: " + json.dumps(resources, sort_keys=True))
sys.exit(return_code)
PY

    python3 - "$report" "$seed" "$pairs" <<'PY'
import json
import sys

path, seed, pairs = sys.argv[1:]
with open(path, encoding="utf-8") as source:
    report = json.load(source)
if report["Player"] != "heuristic-endgame" or report["Opponent"] != "master":
    raise SystemExit(f"Unexpected profiles in {path}.")
if report["Seed"] != int(seed) or report["Pairs"] != int(pairs):
    raise SystemExit(f"Unexpected seed or pair count in {path}.")
if len(report["Result"]["PairScores"]) != int(pairs) or len(report["Result"]["PairPoints"]) != int(pairs):
    raise SystemExit(f"Incomplete pair results in {path}.")
print(f"verified complete: {path}")
PY
    python3 tools/NeuralTrainer/ai01-summarize.py
}

run_timing() {
    local profile="$1"
    local directory="artifacts/AI-01/performance"
    local data="$directory/$profile"
    local metrics="$data.resources.json"
    local log="$data.log"

    mkdir -p "$directory"
    if [[ -e "$metrics" ]]; then
        python3 - "$metrics" "$profile" <<'PY'
import json
import sys

path, profile = sys.argv[1:]
with open(path, encoding="utf-8") as source:
    report = json.load(source)
if report["profile"] != profile or report["return_code"] != 0:
    raise SystemExit(f"Refusing to reuse incomplete timing output: {path}")
print(f"timing already complete: {path}")
PY
        return
    fi

    python3 - "$runner" "$profile" "$data" <<'PY' | tee -a "$log"
import json
import resource
import subprocess
import sys
import time

runner, profile, data = sys.argv[1:]
command = ["dotnet", runner, "timing", "--player", profile, "--bench-games", "20", "--seed", "17001"]
before = resource.getrusage(resource.RUSAGE_CHILDREN)
started = time.monotonic()
process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
output = []
for line in process.stdout:
    output.append(line)
    print(line, end="", flush=True)
return_code = process.wait()
elapsed = time.monotonic() - started
after = resource.getrusage(resource.RUSAGE_CHILDREN)
user = after.ru_utime - before.ru_utime
system = after.ru_stime - before.ru_stime
resources = {
    "profile": profile,
    "bench_games": 20,
    "warmup_games": 20,
    "wall_seconds": elapsed,
    "user_cpu_seconds": user,
    "system_cpu_seconds": system,
    "cpu_percent_of_one_core": (user + system) * 100 / elapsed if elapsed else 0,
    "max_rss_mb": after.ru_maxrss / 1024,
    "return_code": return_code,
    "timing_output": "".join(output),
}
with open(data + ".resources.json", "w", encoding="utf-8") as destination:
    json.dump(resources, destination, indent=2)
    destination.write("\n")
print("resource use: " + json.dumps({key: value for key, value in resources.items() if key != "timing_output"}, sort_keys=True))
sys.exit(return_code)
PY
}

case "$mode" in
    smoke)
        run_batch "smoke" 9000 2
        ;;
    pair)
        if [[ $# -ne 2 || ! "$2" =~ ^[0-9]+$ ]] || (( $2 > 12 )); then
            printf 'Pair batch number must be between 0 and 12.\n' >&2
            exit 2
        fi
        batch="$2"
        pairs=4
        if (( batch == 12 )); then
            pairs=2
        fi
        printf -v name 'pair-%02d' "$batch"
        run_batch "$name" "$((1000 + batch))" "$pairs"
        ;;
    timing)
        run_timing heuristic-endgame
        run_timing master
        python3 tools/NeuralTrainer/ai01-summarize.py
        ;;
    *)
        printf 'Usage: bash tools/NeuralTrainer/ai01-run.sh smoke | pair 0-12 | timing\n' >&2
        exit 2
        ;;
esac
