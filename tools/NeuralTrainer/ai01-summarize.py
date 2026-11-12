#!/usr/bin/env python3
"""Rebuild AI-01's cumulative, resumable report from completed arena batches."""

import csv
import json
import math
import re
from pathlib import Path


ROOT = Path("artifacts/AI-01")
BATCHES = ROOT / "batches"
EXPECTED_PAIRS = 50
EXPECTED_GAMES = 2 * EXPECTED_PAIRS


def load_batches():
    batches = []
    for path in sorted(BATCHES.glob("pair-*.arena.json")):
        with path.open(encoding="utf-8") as source:
            batch = json.load(source)
        if batch["Player"] != "heuristic-endgame" or batch["Opponent"] != "master":
            raise ValueError(f"Unexpected profiles in {path}")
        pairs = int(batch["Pairs"])
        if len(batch["Result"]["PairScores"]) != pairs or len(batch["Result"]["PairPoints"]) != pairs:
            raise ValueError(f"Incomplete pair results in {path}")
        if pairs not in (2, 4):
            raise ValueError(f"Unexpected batch size in {path}: {pairs}")
        batches.append((path, batch))
    return batches


def main():
    batches = load_batches()
    rows = []
    for path, batch in batches:
        seed_base = int(batch["Seed"]) * 100_000
        scores = batch["Result"]["PairScores"]
        points = batch["Result"]["PairPoints"]
        for pair_index, (score, margin) in enumerate(zip(scores, points)):
            rows.append(
                {
                    "batch": path.stem.removesuffix(".arena"),
                    "pair_index_in_batch": pair_index,
                    "deal_seed": seed_base + pair_index,
                    "games": 2,
                    "heuristic_wins": round(score * 2),
                    "neural_wins": 2 - round(score * 2),
                    "heuristic_pair_win_share": score,
                    "heuristic_point_margin_over_two_games": margin,
                }
            )

    ROOT.mkdir(parents=True, exist_ok=True)
    csv_path = ROOT / "pairs.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as destination:
        writer = csv.DictWriter(
            destination,
            fieldnames=list(rows[0]) if rows else [
                "batch",
                "pair_index_in_batch",
                "deal_seed",
                "games",
                "heuristic_wins",
                "neural_wins",
                "heuristic_pair_win_share",
                "heuristic_point_margin_over_two_games",
            ],
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(rows)

    count = len(rows)
    games = count * 2
    wins = sum(row["heuristic_wins"] for row in rows)
    score = wins / games if games else 0.0
    pair_points = [row["heuristic_point_margin_over_two_games"] / 2 for row in rows]
    points = sum(pair_points) / count if count else 0.0
    if count > 1:
        standard_error = math.sqrt(sum((row["heuristic_pair_win_share"] - score) ** 2 for row in rows) / (count - 1) / count)
        point_standard_error = math.sqrt(sum((value - points) ** 2 for value in pair_points) / (count - 1) / count)
    else:
        standard_error = float("nan")
        point_standard_error = float("nan")
    lower = max(0.0, score - 1.959963984540054 * standard_error) if count > 1 else float("nan")
    upper = min(1.0, score + 1.959963984540054 * standard_error) if count > 1 else float("nan")
    point_lower = points - 1.959963984540054 * point_standard_error if count > 1 else float("nan")
    point_upper = points + 1.959963984540054 * point_standard_error if count > 1 else float("nan")

    resources = []
    for path, _ in batches:
        resource_path = path.with_suffix("").with_suffix(".resources.json")
        if resource_path.exists():
            with resource_path.open(encoding="utf-8") as source:
                resources.append(json.load(source))
    resource_summary = (
        f"{sum(item['wall_seconds'] for item in resources):.2f}s wall, "
        f"{sum(item['user_cpu_seconds'] + item['system_cpu_seconds'] for item in resources):.2f}s CPU, "
        f"{max(item['max_rss_mb'] for item in resources):.1f}MB peak RSS"
        if resources
        else "not measured yet"
    )
    interval = f"[{lower:.1%}, {upper:.1%}]" if count > 1 else "not estimable"
    smoke_path = BATCHES / "smoke.arena.json"
    smoke_line = "Smoke check: not found.\n\n"
    if smoke_path.exists():
        with smoke_path.open(encoding="utf-8") as source:
            smoke = json.load(source)
        smoke_result = smoke["Result"]["Match"]
        smoke_line = (
            f"Smoke check (separate, excluded): {smoke_result['Games']} games, "
            f"{smoke_result['Score']:.1%} heuristic win share, "
            f"{smoke_result['PointsPerGame']:+.1f} points/game, "
            f"{smoke['Seconds']:.2f}s; resource capture was unavailable for this run.\n\n"
        )
    result = (
        "# AI-01 focused Master comparison\n\n"
        f"Status: **{count}/{EXPECTED_PAIRS} paired comparisons** ({games}/{EXPECTED_GAMES} games).\n\n"
        + smoke_line
        + "Team A: `ClaudePlayerProfiles.CreateHeuristic()` (`heuristic-endgame`). "
        "Team B: `ClaudePlayerProfiles.CreateMaster()` (`master`). Each pair uses one seeded deal "
        "twice with the teams swapped; Arena records a pair win share and the summed point margin. "
        "Deal seeds are `100000 * SeedArgument + pairIndexInBatch`; the per-batch JSON contains "
        "the argument and raw pair arrays. Batches are four pairs except the final two-pair batch. "
        "Smoke-test results are separate and excluded.\n\n"
        f"- Heuristic win share (draws counted as half, if any): **{score:.1%}**; approximate "
        f"95% paired interval: **{interval}**.\n"
        f"- Neural Master win share: **{1 - score:.1%}**.\n"
        f"- Heuristic point margin: **{points:+.2f} points/game**, approximate paired 95% interval "
        f"**[{point_lower:+.2f}, {point_upper:+.2f}]**.\n"
        f"- Resources for measured arena batches: {resource_summary}.\n"
        "- Uncertainty uses the sample standard error across paired comparisons with a normal "
        "95% interval; it is descriptive, not a significance claim. The sample is small and "
        "cannot establish broad consistency.\n"
        "- Historical context (not pooled): `HEURISTIC_PLAYER.md` reports 34.3% for the "
        "heuristic-endgame player against the human-style Master; the summary table does not give "
        "that matchup's game count.\n\n"
        "## Completed batches\n\n"
        "| Batch | Seed argument | Deal seed range | Pairs | Games | Wall time | Peak RSS |\n"
        "|---|---:|---:|---:|---:|---:|---:|\n"
    )
    for path, batch in batches:
        seed_arg = int(batch["Seed"])
        pairs = int(batch["Pairs"])
        resource_path = path.with_suffix("").with_suffix(".resources.json")
        if resource_path.exists():
            with resource_path.open(encoding="utf-8") as source:
                measured = json.load(source)
            wall = f"{measured['wall_seconds']:.2f}s"
            rss = f"{measured['max_rss_mb']:.1f}MB"
        else:
            wall = rss = "not measured"
        result += (
            f"| {path.stem.removesuffix('.arena')} | {seed_arg} | "
            f"{seed_arg * 100_000}–{seed_arg * 100_000 + pairs - 1} | "
            f"{pairs} | {pairs * 2} | {wall} | {rss} |\n"
        )
    performance = []
    for profile in ("heuristic-endgame", "master"):
        metrics_path = ROOT / "performance" / f"{profile}.resources.json"
        if not metrics_path.exists():
            continue
        with metrics_path.open(encoding="utf-8") as source:
            metrics = json.load(source)
        decision = re.search(
            r"decision ms: mean=([\d.]+).*?max=([\d.]+)",
            metrics["timing_output"],
        )
        card = re.search(
            r"card ms: mean=([\d.]+).*?max=([\d.]+)",
            metrics["timing_output"],
        )
        if decision is None or card is None:
            raise ValueError(f"Could not parse latency output in {metrics_path}")
        performance.append(
            f"| {profile} | {float(decision.group(1)):.3f} | {float(decision.group(2)):.3f} | "
            f"{float(card.group(1)):.3f} | {float(card.group(2)):.3f} | "
            f"{metrics['cpu_percent_of_one_core']:.1f}% | {metrics['max_rss_mb']:.1f}MB | "
            f"{metrics['wall_seconds']:.2f}s |\n"
        )
    if performance:
        result += (
            "\n## Decision latency and resources\n\n"
            "Each profile was timed in 20 measured whole games after 20 warmup games, with all four "
            "seats using that profile on one thread. Decision latency includes bid, announce and card "
            "callbacks; forced-card callbacks are included. CPU is expressed as a percentage of one "
            "core for the whole process; RSS includes the .NET runtime and embedded model loading.\n\n"
            "| Profile | All-decision mean (ms) | All-decision max (ms) | Card mean (ms) | Card max (ms) | CPU | Peak RSS | Wall |\n"
            "|---|---:|---:|---:|---:|---:|---:|---:|\n"
            + "".join(performance)
        )
    compatibility_path = ROOT / "performance" / "render-compatibility.json"
    if compatibility_path.exists():
        with compatibility_path.open(encoding="utf-8") as source:
            compatibility = json.load(source)
        constrained = compatibility["neural_arena_smoke"]
        peak_mib = constrained["cgroup_memory_peak_bytes"] / (1024 * 1024)
        result += (
            "\n## Docker and Render Free checks\n\n"
            f"- The production Dockerfile built successfully with the .NET 10 ASP.NET runtime. "
            f"Under **{compatibility['limits']['cpu']} / {compatibility['limits']['memory']}**, "
            f"the local API returned health `{compatibility['api_health']}`, exposed the Master "
            f"profile and created a game; its observed current RSS was "
            f"{compatibility['api_container_current_rss_mib']:.2f} MiB. The mapping remains "
            "`CreateHeuristic()`; this only verifies API/container startup and routes.\n"
            f"- A separate four-game arena smoke loaded and ran both profiles in the same CPU/RAM "
            f"limits in {constrained['wall_seconds']:.2f}s, with {peak_mib:.1f} MiB cgroup peak "
            "memory. This is a resource check only and is excluded from the strength sample.\n"
        )
    result += f"\nRaw batch JSON and logs are in `{BATCHES}/`; per-pair results are in `pairs.csv`.\n"
    (ROOT / "progress.md").write_text(result, encoding="utf-8")
    print(f"updated {ROOT / 'progress.md'} and {csv_path}: {count}/{EXPECTED_PAIRS} pairs")


if __name__ == "__main__":
    main()
