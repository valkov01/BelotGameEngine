# AI-01 focused Master comparison

Status: **50/50 paired comparisons** (100/100 games).

Smoke check (separate, excluded): 4 games, 50.0% heuristic win share, +0.0 points/game, 9.56s; resource capture was unavailable for this run.

Team A: `ClaudePlayerProfiles.CreateHeuristic()` (`heuristic-endgame`). Team B: `ClaudePlayerProfiles.CreateMaster()` (`master`). Each pair uses one seeded deal twice with the teams swapped; Arena records a pair win share and the summed point margin. Deal seeds are `100000 * SeedArgument + pairIndexInBatch`; the per-batch JSON contains the argument and raw pair arrays. Batches are four pairs except the final two-pair batch. Smoke-test results are separate and excluded.

- Heuristic win share (draws counted as half, if any): **42.0%**; approximate 95% paired interval: **[33.9%, 50.1%]**.
- Neural Master win share: **58.0%**.
- Heuristic point margin: **-20.09 points/game**, approximate paired 95% interval **[-28.66, -11.52]**.
- Resources for measured arena batches: 227.81s wall, 231.46s CPU, 75.0MB peak RSS.
- Uncertainty uses the sample standard error across paired comparisons with a normal 95% interval; it is descriptive, not a significance claim. The sample is small and cannot establish broad consistency.
- Historical context (not pooled): `HEURISTIC_PLAYER.md` reports 34.3% for the heuristic-endgame player against the human-style Master; the summary table does not give that matchup's game count.

## Completed batches

| Batch | Seed argument | Deal seed range | Pairs | Games | Wall time | Peak RSS |
|---|---:|---:|---:|---:|---:|---:|
| pair-00 | 1000 | 100000000–100000003 | 4 | 8 | 18.41s | 72.2MB |
| pair-01 | 1001 | 100100000–100100003 | 4 | 8 | 20.22s | 74.9MB |
| pair-02 | 1002 | 100200000–100200003 | 4 | 8 | 17.64s | 74.6MB |
| pair-03 | 1003 | 100300000–100300003 | 4 | 8 | 19.26s | 74.3MB |
| pair-04 | 1004 | 100400000–100400003 | 4 | 8 | 17.63s | 74.6MB |
| pair-05 | 1005 | 100500000–100500003 | 4 | 8 | 18.55s | 74.6MB |
| pair-06 | 1006 | 100600000–100600003 | 4 | 8 | 18.45s | 71.8MB |
| pair-07 | 1007 | 100700000–100700003 | 4 | 8 | 17.67s | 74.9MB |
| pair-08 | 1008 | 100800000–100800003 | 4 | 8 | 15.84s | 72.2MB |
| pair-09 | 1009 | 100900000–100900003 | 4 | 8 | 17.80s | 74.1MB |
| pair-10 | 1010 | 101000000–101000003 | 4 | 8 | 17.36s | 75.0MB |
| pair-11 | 1011 | 101100000–101100003 | 4 | 8 | 18.69s | 75.0MB |
| pair-12 | 1012 | 101200000–101200001 | 2 | 4 | 10.28s | 71.1MB |

## Decision latency and resources

Each profile was timed in 20 measured whole games after 20 warmup games, with all four seats using that profile on one thread. Decision latency includes bid, announce and card callbacks; forced-card callbacks are included. CPU is expressed as a percentage of one core for the whole process; RSS includes the .NET runtime and embedded model loading.

| Profile | All-decision mean (ms) | All-decision max (ms) | Card mean (ms) | Card max (ms) | CPU | Peak RSS | Wall |
|---|---:|---:|---:|---:|---:|---:|---:|
| heuristic-endgame | 0.995 | 9.814 | 1.347 | 9.814 | 101.6% | 59.5MB | 13.59s |
| master | 12.869 | 65.300 | 17.466 | 65.300 | 99.4% | 62.7MB | 145.52s |

## Docker and Render Free checks

- The production Dockerfile built successfully with the .NET 10 ASP.NET runtime. Under **0.1 CPU / 512 MB**, the local API returned health `ok`, exposed the Master profile and created a game; its observed current RSS was 37.42 MiB. The mapping remains `CreateHeuristic()`; this only verifies API/container startup and routes.
- A separate four-game arena smoke loaded and ran both profiles in the same CPU/RAM limits in 63.19s, with 167.3 MiB cgroup peak memory. This is a resource check only and is excluded from the strength sample.

Raw batch JSON and logs are in `artifacts/AI-01/batches/`; per-pair results are in `pairs.csv`.
