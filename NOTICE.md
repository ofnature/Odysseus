# Notices and third-party data

Odysseus is GPL-3.0 (see `COPYING`, `COPYING.LESSER`). It replaces the job of several plugins, so it
touches several projects' work. This file records what comes from where, and on what terms — kept
current as sources are added, because the whole point of consolidating is that nobody else is left
tracking it.

## Imported data, and the licence that follows it

### Questionable — which one matters

Questionable was written by **Liza Carvelli** (`git.carvel.li/liza/Questionable`) under **AGPL-3.0**.
It has since split, and the continuations do not agree on terms — so *which copy a user imports from*
decides what may be done with the result. Checked 2026-08-21:

| repository | licence | may we convert and use it? |
|---|---|---|
| Liza Carvelli's original | AGPL-3.0 | yes, under AGPL terms |
| `PunishXIV/Questionable` — created 2025-08-21, the build in use here (15.306.2.4) | **AGPL-3.0** | yes, under AGPL terms |
| `WigglyMuffin/Questionable` — "Continuation of Questionable - Originally by Liza Carvelli" | **proprietary from 6.9 onward** (6.8 and earlier were AGPL-3.0) | **no** |
| `pot0to/Questionable` (2024) | none stated; describes itself as a fork of liza's | treat as AGPL, from the original |

WigglyMuffin's licence forbids exactly what an importer does. Verbatim: the licensee shall not
*"modify, adapt, translate, or create derivative works based on the Software"*, nor
*"redistribute, publish, sell, lease, rent, lend, sublicense, assign, or otherwise transfer"* it. A
format conversion is a derivative work, so importing paths from that build is not a grey area — it
is prohibited, whoever does it and wherever the result stays. Its 6.8 and earlier releases remain
AGPL-3.0.

**Odysseus therefore imports from an AGPL-licensed Questionable only.** The importer should say which
build it read, so this stays checkable rather than assumed.

### Quest paths — from Questionable, converted

The quest paths Odysseus runs are imported from **Questionable**, originally by **Liza Carvelli** and
continued at `PunishXIV/Questionable` under AGPL-3.0. Odysseus reads that project's path bundle and
converts it into its own format. The routes, waypoints, NPC ids and step sequences are their work,
and the thanks for them belongs there.

The conversion is a derivative of their data, and Odysseus is **AGPL-3.0 for this reason** — a
project that runs on AGPL data cannot honour it under weaker terms. Odysseus moved from LGPL-3.0 to
AGPL-3.0 on 2026-08-21 to keep the chain intact.

As AGPL-3.0 §5(a) requires: **the imported data is modified.** It is translated into a different
schema, and steps are adjusted where running them here needs something the source shape does not
express — flight suppressed for allied society paths in base-game zones, per-step dismounts, added
waypoints, corrections made while running a path. Those changes are Odysseus's, not Questionable's,
and should not be laid at their door.

Odysseus imports from an AGPL-licensed Questionable only; see the table above.

### What is and is not in the download

No path library is shipped today. AGPL-3.0 permits it — with attribution, the licence text, and the
modification notice above — so this is now a choice rather than a bar. Until it is made, the
importer converts a bundle the user supplies and the result stays on their machine, the paths folder
is shareable across installs (Settings), and an export exists for carrying your own copy between
your own machines.

For the record: **v0.1.1 (2026-08-20) shipped `Assets/paths.pak`**, 4,240 converted quest paths,
while Odysseus was still LGPL-3.0 and carried no attribution to Questionable. That was an oversight.
It was removed from the build, and the relicensing here is what would make shipping it legitimate.

### QuestFlow (`RoseOfficial/QuestFlow`)

Publishes no licence file; its README refers to one that does not exist. Its gathering paths are
credited to liza, Theo, Censored and plogon_enjoyer and point at liza's own repository — the same
authors and the same data as Questionable, and so AGPL-3.0 whatever the republication says.
**Not used as a shipped source.**

## Data read at runtime from the user's own installed plugins

### AutoHook (`PunishedPineapple/AutoHook`)

Bait, mooch chains and hook timings are read from `Data/FishData/fish_list.json` inside the user's
own AutoHook installation, at runtime. Nothing is copied into Odysseus and nothing is shipped. If
AutoHook is not installed, the fishing features say so rather than falling back to a copy.

## Data that may be shipped, with attribution

### GatherBuddy / GatherBuddy Reborn — Apache License 2.0

- `Ottermandias/GatherBuddy` — Apache 2.0
- `FFXIV-CombatReborn/GatherBuddyReborn` — Apache 2.0

Node coordinates originate in `GatherBuddy.CustomInfo.world_locations.json`. Apache 2.0 permits
redistribution, including of the data, provided a copy of the licence travels with it, the original
notices are retained, and modified files are marked as changed. Any Odysseus release carrying this
data must ship `licenses/Apache-2.0.txt` and state that the coordinates come from GatherBuddy and
what was changed (format conversion; selection by delivery item).

Apache 2.0 is one-way compatible with GPL-3, so it can be combined into this project. The reverse is
not true, and nothing here is contributed back under Apache terms.

## Handoffs, not data

Artisan, GatherBuddy Reborn, vnavmesh, Lifestream, TextAdvance, BossMod Reborn, Theseus and
Questionable are driven over their published IPC where the user has them installed. Calling a
plugin's IPC is use, not derivation, and none of their code is included here.
