#!/usr/bin/env python3
"""Run bounded HTTP checks against a locally constrained AI-01 API container."""

from __future__ import annotations

import concurrent.futures
import json
import math
import re
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Callable


ROOT = Path(__file__).resolve().parents[2]
RESULTS = ROOT / "artifacts" / "AI-01" / "api-performance"
CONTAINER = "belot-ai01-neural"
IMAGE = "belot-ai01-neural-ai01"
BASE_URL = "http://127.0.0.1:10000"
CREATE_REQUEST = {"north": "master", "east": "master", "west": "master"}
CONCURRENCY = 4


def docker(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["docker", *args],
        check=check,
        capture_output=True,
        text=True,
    )


def cgroup_snapshot() -> dict[str, int] | None:
    result = docker(
        "exec",
        CONTAINER,
        "sh",
        "-c",
        "cat /sys/fs/cgroup/cpu.stat; "
        "printf '\\n--memory--\\n'; "
        "cat /sys/fs/cgroup/memory.current /sys/fs/cgroup/memory.peak",
        check=False,
    )
    if result.returncode:
        return None

    cpu_stats, memory_stats = result.stdout.split("--memory--\n")
    values = {}
    for line in cpu_stats.splitlines():
        if not line.strip():
            continue
        key, value = line.split()
        values[key] = int(value)
    memory_values = [int(value) for value in memory_stats.splitlines() if value.strip()]
    values["memory_current"], values["memory_peak"] = memory_values
    return values


def parse_memory(value: str) -> float:
    match = re.match(r"\s*([\d.]+)\s*(B|KiB|MiB|GiB)", value)
    if not match:
        raise ValueError(f"Unrecognized Docker memory value: {value!r}")
    amount = float(match.group(1))
    multiplier = {"B": 1, "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3}[match.group(2)]
    return amount * multiplier


class ResourceMonitor:
    def __init__(self) -> None:
        self.samples: list[dict[str, float]] = []
        self.errors: list[str] = []
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self.sample, daemon=True)

    def start(self) -> None:
        self.thread.start()

    def stop(self) -> None:
        self.stop_event.set()
        self.thread.join()

    def sample(self) -> None:
        while not self.stop_event.is_set():
            result = docker(
                "stats",
                "--no-stream",
                "--format",
                "{{.CPUPerc}},{{.MemUsage}}",
                CONTAINER,
                check=False,
            )
            if result.returncode:
                self.errors.append(result.stderr.strip())
                if self.stop_event.wait(0.2):
                    return
                continue
            try:
                cpu_percent, memory_usage = result.stdout.strip().split(",", 1)
                memory_used = memory_usage.split("/", 1)[0].strip()
                self.samples.append(
                    {
                        "cpu_percent": float(cpu_percent.rstrip("%")),
                        "memory_bytes": parse_memory(memory_used),
                    }
                )
            except (ValueError, IndexError) as error:
                self.errors.append(str(error))
            if self.stop_event.wait(0.2):
                return

    def summary(
        self,
        before: dict[str, int] | None,
        after: dict[str, int] | None,
        wall_seconds: float | None = None,
    ) -> dict[str, Any]:
        result: dict[str, Any] = {
            "docker_stats_sample_count": len(self.samples),
            "sampled_peak_memory_mib": round(
                max((sample["memory_bytes"] for sample in self.samples), default=0) / 1024**2,
                2,
            ),
            "sampled_peak_cpu_percent": round(
                max((sample["cpu_percent"] for sample in self.samples), default=0),
                2,
            ),
            "docker_stats_errors": self.errors[:3],
        }
        if after:
            result["cgroup_peak_memory_mib"] = round(after["memory_peak"] / 1024**2, 2)
            result["cgroup_memory_current_mib"] = round(after["memory_current"] / 1024**2, 2)
        if before and after:
            elapsed_cpu = after["usage_usec"] - before["usage_usec"]
            result.update(
                {
                    "cgroup_cpu_seconds": round(elapsed_cpu / 1_000_000, 3),
                    "cgroup_cpu_cores_average": round(
                        elapsed_cpu / 1_000_000 / wall_seconds,
                        4,
                    ) if wall_seconds else None,
                    "cgroup_throttled_usec": after.get("throttled_usec", 0) - before.get("throttled_usec", 0),
                    "cgroup_throttled_periods": after.get("nr_throttled", 0) - before.get("nr_throttled", 0),
                    "cgroup_total_periods": after.get("nr_periods", 0) - before.get("nr_periods", 0),
                }
            )
        return result


def percentile95(values: list[float]) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * 0.95) - 1)]


def latency_summary(values: list[float]) -> dict[str, float | int]:
    if not values:
        raise ValueError("A latency summary requires at least one sample.")
    return {
        "n": len(values),
        "median_ms": round(statistics.median(values), 2),
        "p95_ms": round(percentile95(values), 2),
        "max_ms": round(max(values), 2),
    }


def request_json(path: str, body: dict[str, Any] | None = None) -> tuple[dict[str, Any], float]:
    payload = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(
        BASE_URL + path,
        data=payload,
        headers={"Content-Type": "application/json"} if payload is not None else {},
        method="GET" if payload is None else "POST",
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            if response.status not in (200, 201):
                raise RuntimeError(f"Unexpected HTTP {response.status} for {path}.")
            return json.loads(response.read()), (time.perf_counter() - started) * 1000
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"HTTP {error.code} for {path}: {error.read().decode()}") from error


def request_first_available(path: str, body: dict[str, Any]) -> tuple[dict[str, Any], float]:
    started = time.perf_counter()
    last_error: Exception | None = None
    for _ in range(120):
        try:
            data, elapsed = request_json(path, body)
            return data, elapsed
        except (urllib.error.URLError, ConnectionError, TimeoutError) as error:
            last_error = error
            time.sleep(0.1)
    raise RuntimeError("API did not accept its first request within 12 seconds.") from last_error


def create_game() -> tuple[dict[str, Any], float]:
    data, elapsed = request_json("/api/games", CREATE_REQUEST)
    return data, elapsed


def action(game_id: str, body: dict[str, Any]) -> tuple[dict[str, Any], float]:
    data, elapsed = request_json(f"/api/games/{game_id}/actions", body)
    return data, elapsed


def all_trumps_bid(state: dict[str, Any]) -> dict[str, str]:
    bids = state["availableBids"]
    if "AllTrumps" not in bids:
        raise RuntimeError(f"AllTrumps is not available in the initial auction: {bids!r}")
    return {"bid": "AllTrumps"}


def opening_bid(state: dict[str, Any]) -> dict[str, str]:
    bids = state["availableBids"]
    if "Clubs" not in bids:
        raise RuntimeError(f"Clubs is not available in the initial auction: {bids!r}")
    return {"bid": "Clubs"}


def next_human_action(state: dict[str, Any]) -> dict[str, Any]:
    decision = state["decision"]
    if decision == "Bid":
        bids = state["availableBids"]
        return {"bid": "Pass" if "Pass" in bids else bids[0]}
    if decision == "Announce":
        return {"announces": []}
    if decision == "PlayCard":
        card = state["playableCards"][0]
        return {"card": card, "belote": card in state["beloteCards"]}
    raise RuntimeError(f"Unexpected decision: {decision!r}")


def drive_to_play(state: dict[str, Any]) -> dict[str, Any]:
    game_id = state["gameId"]
    for _ in range(100):
        if state["decision"] == "PlayCard":
            return state
        response, _ = action(game_id, next_human_action(state))
        state = response["state"]
    raise RuntimeError("Did not reach a card-play decision within 100 human actions.")


def save_result(phase: str, result: dict[str, Any]) -> None:
    RESULTS.mkdir(parents=True, exist_ok=True)
    path = RESULTS / f"{phase}.json"
    version = 2
    while path.exists():
        path = RESULTS / f"{phase}-{version:02}.json"
        version += 1
    path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"saved": str(path), **result}, indent=2))


def run_measured_phase(phase: str, operation: Callable[[], dict[str, Any]]) -> None:
    resource_started = time.perf_counter()
    before = cgroup_snapshot()
    monitor = ResourceMonitor()
    monitor.start()
    started = time.perf_counter()
    try:
        result = operation()
    finally:
        wall_seconds = time.perf_counter() - started
        monitor.stop()
    after = cgroup_snapshot()
    resource_wall_seconds = time.perf_counter() - resource_started
    result.update(
        {
            "phase": phase,
            "wall_seconds": round(wall_seconds, 2),
            "resources": monitor.summary(before, after, resource_wall_seconds),
            "limits": {"cpus": 0.1, "memory_mib": 512},
            "image": IMAGE,
        }
    )
    save_result(phase, result)


def cold_start() -> None:
    started = time.perf_counter()
    docker(
        "run",
        "-d",
        "--name",
        CONTAINER,
        "--cpus=0.1",
        "--memory=512m",
        "-p",
        "127.0.0.1:10000:10000",
        IMAGE,
    )
    monitor = ResourceMonitor()
    monitor.start()
    try:
        state, create_ms = request_first_available("/api/games", CREATE_REQUEST)
        startup_to_create_ms = (time.perf_counter() - started) * 1000
        bid_response, bid_ms = action(state["gameId"], opening_bid(state))
    finally:
        monitor.stop()
    result = {
        "phase": "cold-start",
        "limits": {"cpus": 0.1, "memory_mib": 512},
        "image": IMAGE,
        "docker_start_to_game_created_ms": round(startup_to_create_ms, 2),
        "first_game_create_response_ms": round(create_ms, 2),
        "first_neural_bid_response_ms": round(bid_ms, 2),
        "first_bid_automatic_bot_bids": sum(
            event["type"] == "bid" and event.get("automatic") is True
            for event in bid_response["events"]
        ),
        "first_bid_decision": bid_response["state"]["decision"],
        "resources": monitor.summary(None, cgroup_snapshot()),
    }
    save_result("cold-start", result)


def warm_create() -> dict[str, Any]:
    timings = [create_game()[1] for _ in range(20)]
    return {"latency_ms": {"game_create": latency_summary(timings)}, "samples_ms": timings}


def automatic_bids() -> dict[str, Any]:
    timings = []
    bot_bids = []
    bot_bid_types = []
    for _ in range(10):
        state, _ = create_game()
        response, elapsed = action(state["gameId"], opening_bid(state))
        timings.append(elapsed)
        automatic = [
            event for event in response["events"]
            if event["type"] == "bid" and event.get("automatic") is True
        ]
        bot_bids.append(len(automatic))
        bot_bid_types.append([event["bid"] for event in automatic])
    return {
        "latency_ms": {"human_bid_plus_automatic_bots": latency_summary(timings)},
        "samples_ms": timings,
        "automatic_bot_bids_per_request": bot_bids,
        "automatic_bot_bid_types": bot_bid_types,
    }


def card_play() -> dict[str, Any]:
    timings = []
    automatic_cards = []
    for _ in range(10):
        state, _ = create_game()
        response, _ = action(state["gameId"], all_trumps_bid(state))
        state = drive_to_play(response["state"])
        card = state["playableCards"][0]
        response, elapsed = action(
            state["gameId"],
            {"card": card, "belote": card in state["beloteCards"]},
        )
        timings.append(elapsed)
        automatic_cards.append(
            sum(event["type"] == "card" and event.get("automatic") is True for event in response["events"])
        )
    return {
        "latency_ms": {"human_card_plus_automatic_bots": latency_summary(timings)},
        "samples_ms": timings,
        "automatic_bot_cards_per_request": automatic_cards,
    }


def complete_round() -> dict[str, Any]:
    timings = []
    actions_per_round = []
    for _ in range(3):
        state, _ = create_game()
        response, _ = action(state["gameId"], all_trumps_bid(state))
        state = response["state"]
        starting_rounds = len(state["previousRounds"])
        action_count = 1
        for _ in range(500):
            if len(state["previousRounds"]) > starting_rounds or state["isFinished"]:
                break
            response, elapsed = action(state["gameId"], next_human_action(state))
            state = response["state"]
            action_count += 1
        if len(state["previousRounds"]) == starting_rounds:
            raise RuntimeError("A complete round was not reached within 500 human actions.")
        timings.append(elapsed)
        actions_per_round.append(action_count)
    return {
        "latency_ms": {"round_completing_http_request": latency_summary(timings)},
        "samples_ms": timings,
        "human_actions_to_round_completion": actions_per_round,
    }


def concurrent_games() -> dict[str, Any]:
    states = [create_game()[0] for _ in range(CONCURRENCY)]

    def bid(state: dict[str, Any]) -> tuple[dict[str, Any], float, int]:
        response, elapsed = action(state["gameId"], opening_bid(state))
        count = sum(event["type"] == "bid" and event.get("automatic") is True for event in response["events"])
        return response["state"], elapsed, count

    with concurrent.futures.ThreadPoolExecutor(max_workers=CONCURRENCY) as executor:
        bid_results = list(executor.map(bid, states))
    ready_states = [drive_to_play(result[0]) for result in bid_results]

    def play(state: dict[str, Any]) -> tuple[float, int]:
        card = state["playableCards"][0]
        response, elapsed = action(
            state["gameId"],
            {"card": card, "belote": card in state["beloteCards"]},
        )
        count = sum(event["type"] == "card" and event.get("automatic") is True for event in response["events"])
        return elapsed, count

    with concurrent.futures.ThreadPoolExecutor(max_workers=CONCURRENCY) as executor:
        card_results = list(executor.map(play, ready_states))
    bid_timings = [result[1] for result in bid_results]
    card_timings = [result[0] for result in card_results]
    return {
        "concurrency": CONCURRENCY,
        "latency_ms": {
            "parallel_bid_plus_automatic_bots": latency_summary(bid_timings),
            "parallel_card_plus_automatic_bots": latency_summary(card_timings),
        },
        "parallel_bid_samples_ms": bid_timings,
        "parallel_card_samples_ms": card_timings,
        "automatic_bot_bids_per_request": [result[2] for result in bid_results],
        "automatic_bot_cards_per_request": [result[1] for result in card_results],
    }


def require_running_container() -> None:
    result = docker("inspect", "-f", "{{.State.Running}}", CONTAINER, check=False)
    if result.returncode or result.stdout.strip() != "true":
        raise RuntimeError(f"Container {CONTAINER!r} is not running.")


def main() -> None:
    phases = {
        "cold": cold_start,
        "create": lambda: run_measured_phase("game-create", warm_create),
        "bids": lambda: run_measured_phase("automatic-bids", automatic_bids),
        "cards": lambda: run_measured_phase("card-play", card_play),
        "round": lambda: run_measured_phase("round-complete", complete_round),
        "concurrent": lambda: run_measured_phase("concurrent-games", concurrent_games),
    }
    if len(sys.argv) != 2 or sys.argv[1] not in phases:
        choices = ", ".join(phases)
        raise SystemExit(f"Usage: {Path(sys.argv[0]).name} <{choices}>")
    if sys.argv[1] != "cold":
        require_running_container()
    phases[sys.argv[1]]()


if __name__ == "__main__":
    main()
