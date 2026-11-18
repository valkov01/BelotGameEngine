# SportBelot C# engine and Web API — Copilot repository instructions

These instructions apply to `valkov01/BelotGameEngine`. The canonical **cross-repository** status, approvals and NEXT ACTION live in `valkov01/belote-bridge-battle/docs/PROJECT_ROADMAP.md` and its linked GitHub issues. Verify current branch/code and comments before coding; do not treat an older architecture document's future-tense sections as evidence of what is currently implemented.

## Architecture and safety boundaries

- `Belot.Engine` / `BelotMatch` is the authoritative source for legal bids/cards, tricks, board progression, bot decisions and scoring. Keep the engine independent of ASP.NET, React, Supabase and SignalR.
- Preserve the existing live Solo API and its contract, including the five profile IDs `random`, `beginner`, `skilled`, `expert`, `master`. Do not change bot internals or Master mapping for an unrelated feature.
- Engine seats are absolute South, East, North, West; play order is counterclockwise South → East → North → West; fixed partnerships South+North against East+West. Client-side viewer rotation does not change these identities.
- Multiplayer commands require validated Supabase identity, membership/seat ownership, revision/idempotency and server-side legal-action checks. REST returns seat-scoped private snapshots; SignalR must emit only allowed sequenced public events. Never send other players' private hands, rely on client-supplied seat/owner IDs or log bearer tokens.
- A10 Team Matches are two tables/eight fixed seats with matching numbered deals and server-authoritative Total Points. Follow the approved specification in the frontend repository's `docs/team-matches-spec-v0.1.md`; do not implement A09 MP/IMP scoring without explicit Product Owner sign-off. Cross-table barometer disclosures must be gated on the server, not just hidden in the UI.
- Live sessions are currently in-memory. Never claim persistence across Render restarts until implemented and tested; see frontend issues #12 and #13.
- Developer hand-reveal functionality is privileged/test-only. Do not enable it for regular users or disclose debug secrets.
- Do not weaken global abuse prevention, disable rate limiting, widen CORS to wildcard origins or change JWT trust settings for convenience. For reported HTTP 429, verify actual deployed configuration, caller identity/IP partition and request volume before proposing an authorization-aware scoped fix.

## Working procedure

1. Confirm repository branch and relevant GitHub issue/PR; read the actual current implementation, architecture and tests before making changes. Multiplayer staging development uses `a02-multiplayer-foundation`; `master` hosts the existing Solo baseline.
2. Keep changes small and testable; separate engine behavior from API adapters, operational configuration and frontend UX.
3. Maintain Solo contract compatibility and secure failure behavior. Include negative-path tests for authentication, seat authorization, private-data leaks and stale or duplicated actions when relevant.
4. Build and run applicable .NET tests (e.g. `dotnet test` with the appropriate solution/project); report exact commands, pass/fail totals and unexecuted checks. Do not equate source tests, staging deployment and browser acceptance.
5. Never deploy to Render, merge branches, alter production secrets or change infrastructure settings without explicit owner approval. Keep secrets out of source, issue comments, CI logs and documentation.
6. At completion, report files changed, tests and evidence, compatibility risks, unverified scenarios, commit/PR and the next action. Update the existing tracking issue/roadmap at meaningful checkpoints when authorized.

## Relevant source documents

- `src/Belot.WebApi/README.md` — Solo Web API usage, safety and configuration.
- `MULTIPLAYER_ARCHITECTURE_A02.md` on `a02-multiplayer-foundation` — approved starting architecture, not a substitute for current code and newer issues.
- Frontend issues #33 (A10.5 acceptance), #40 (429 stabilization), #42 (public event playback/Team TT UI), and #34 (reliability tests).
