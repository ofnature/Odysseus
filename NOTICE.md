# Notices and third-party data

Odysseus is AGPL-3.0 (see `COPYING`). It replaces the job of several plugins, so it
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

### What is in the download

**From v0.2.1 the converted path library ships** as `Assets/paths.pak`, under AGPL-3.0 with this
notice. It is built for each release from the bundle an installed **PunishXIV/Questionable** (AGPL)
downloaded — currently bundle `1789898831`, generated 2026-09-20T10:08:25Z, 4,324 quests (v0.2.1
shipped bundle `1787426647`, generated 2026-08-22T19:25:29Z, 4,239 quests) — and converted with
the importer in that release. The WigglyMuffin build's bundle is never used, for the
reason in the table above. The importer logs the bundle id it read, so this stays checkable.

History: v0.1.1 (2026-08-20) shipped the pack while Odysseus was still LGPL-3.0 and carried no
attribution to Questionable — an oversight. v0.2.0 shipped none while the licence was put right.
The relicensing to AGPL-3.0 and this notice are what make shipping it legitimate.

### QuestFlow (`RoseOfficial/QuestFlow`)

Publishes no licence file; its README refers to one that does not exist. Its gathering paths are
credited to liza, Theo, Censored and plogon_enjoyer and point at liza's own repository — the same
authors and the same data as Questionable, and so AGPL-3.0 whatever the republication says.
**Not used as a shipped source.**

## Data no longer shipped here

### GatherBuddy / GatherBuddy Reborn — Apache License 2.0

Odysseus shipped GatherBuddy's node coordinates (`world_locations.json`, Apache 2.0) with its own
gatherer up to v0.2.23. The gatherer moved to **Demeter** (`ofnature/Demeter`) on 2026-10-22, and
the data, its licence and its attribution went with it. Nothing from GatherBuddy ships with Odysseus
any more. (The AutoHook fish data that the gatherer's fishing start read at runtime went too.)

## Code studied, code ported

### Questionable (`PunishXIV/Questionable`) — AGPL-3.0

Beyond the path data above, the Dive step's mechanism — reading the game's own MOVE_DESCENT
keybind from its input data and pressing it as window key messages spread over frames — is ported
from Questionable's Dive task. AGPL-3.0 into AGPL-3.0, which this notice records.

### ECommons (`NightmareXIV/ECommons`) — MIT

The subtitle-box click in `GameStepWorld.AdvanceTalk` is ported from ECommons'
`AddonMaster.Talk.Click` (MIT, © 2023 NightmareXIV). MIT permits this with attribution, which this
notice is.

### CBT (`Jaksuhn/ffxiv-bundleoftweaks`, formerly Automaton) — BSD-3-Clause

`SnipeSkipper` — completing a sniping section by answering the snipe task's hook with a hit — is
ported from CBT's `AutoSnipeQuests` ("Sniper no sniping"), signature credited there to xan.
BSD-3-Clause permits this with its notice kept, reproduced here in full:

> BSD 3-Clause License
>
> Copyright (c) 2023, Puni.sh
>
> Redistribution and use in source and binary forms, with or without
> modification, are permitted provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this
>    list of conditions and the following disclaimer.
>
> 2. Redistributions in binary form must reproduce the above copyright notice,
>    this list of conditions and the following disclaimer in the documentation
>    and/or other materials provided with the distribution.
>
> 3. Neither the name of the copyright holder nor the names of its
>    contributors may be used to endorse or promote products derived from
>    this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
> AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
> IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
> DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
> FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
> DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
> SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
> CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
> OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
> OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### TextAdvance (`NightmareXIV/TextAdvance`) — no licence published

Studied, never copied: it publishes no licence file, so only the game-interface facts it
demonstrates are used — which addon, which button id, which condition flags — and every
implementation here is our own. Its cutscene-ESC (a code hook and patch) is deliberately not
reimplemented and still comes from an installed TextAdvance. Reward picking is Odysseus's own
(`RewardPicker`): the same five choices a player of TextAdvance knows, decided from the game's sheets,
and the choice made by sending the reward window's own canvas its click event through ClientStructs —
no signature. The facts used are the window's value slots (rewards from value 82) and that event.

A `reference/` folder in the working tree may hold copies of third-party sources for study. It is
gitignored: nothing in it is built, linked, or shipped.

### Daedalus (`ofnature/Daedalus`, the Olympus repo) — GPL-3.0, the same author

The edge tab the main window minimizes to (`Windows/Dock/`) is ported from Daedalus's edge dock (re-ported 2026-10-19 for its per-tab edges and layout v2) —
`DockHover`, `DockMath`, `DockLayout`/`DockLayoutShare` unchanged, the window redrawn in Odysseus's
colours. GPL-3.0 code combines with AGPL-3.0. The layout share tag (`EdgeDock.Layout.v2`) is shared
with Daedalus and Charon on purpose, so all three plugins' tabs stack on one edge.

## Handoffs, not data

Artisan, Demeter, GatherBuddy Reborn, vnavmesh, Lifestream, TextAdvance, BossMod Reborn, Theseus and
Questionable are driven over their published IPC where the user has them installed. Calling a
plugin's IPC is use, not derivation, and none of their code is included here.
