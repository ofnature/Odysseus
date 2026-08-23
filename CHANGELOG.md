# Changelog

<!-- LATEST-START -->
## v0.2.3 — 2026-08-23

An afternoon of field-hardening on the Amalj'aa dailies. Every fix below came from a live failure.

### Allied societies
- A day of dailies runs all its objectives first, then makes one trip home and hands everything in from the same visit

### Getting there
- Steps whose business is an object arrive by the object: within reach counts, wherever the recorded mark sits, and a flight ending up to ten yalms above it lands on it
- Every such step dismounts before it acts — a mid-air dismount is the game's own descent, so interactions are pinned to the floor
- Combat lands fifteen yalms out and walks the rest in; nothing pulls from the saddle
- A leg the ground mesh cannot route flies when the path says to (tribe runs stay ground-preferred otherwise); the mesh is rebuilt once when it contradicts itself; off-mesh feet step back onto it before pathing
- A WalkTo the world stops a few yalms short of is a waypoint reached, not a fault

### Quest running
- Steps whose completion flags are already set are skipped — no more chasing a despawned objective the character already did
- The reward-overcap warning ("you will not be able to receive all the following") is answered Yes so the run keeps moving — a Settings toggle holds it for you instead, and delivery turn-ins never answer it, since the delivery planner stops short of the cap on purpose

### Tools
- Path Tools shows where the character stands, with a copy button that produces the path-step JSON snippet
- Odysseus writes its own log (odysseus.log beside the config) — Dalamud's stops at its size cap
- The editor's current-step marker is a plain arrow every font can draw
<!-- LATEST-END -->

## v0.2.2 — 2026-08-23

### Quest running
- A destination the mesh does not cover — an NPC's platform painted non-walkable, like Hamujj Gah's — is reached by pathing to the nearest point the mesh does reach and walking the last few yalms directly, instead of faulting "no path" three times
- When "no path" is final, the message now says whether the loaded mesh fails to cover where you stand (stale mesh: `/vnav rebuild`, then Retry) or has no route to the destination
- Overworld combat walks to a mob that is standing off before engaging, and honours a step's kill count (from v0.2.1's fix, noted again here because 0.2.1 shipped minutes earlier)

## v0.2.1 — 2026-08-23

### Paths
- **The quest-path library ships again** — 4,239 quests converted from the PunishXIV (AGPL-3.0) Questionable bundle of 2026-08-22, with attribution in NOTICE.md. v0.2.0 shipped none, which left every install that had not imported its own paths with nothing to run
- The shipped conversion is current (format 4, with Land); a client's own stored copy from an older build yields to it for the same quest. Re-import to refresh your own — hand edits in an older-format copy are superseded either way

### Quest running
- Overworld combat walks to a mob that is standing off before engaging, and stops the approach when the fight starts
- A step's kill count is honoured: it keeps pulling, and waits for the respawn, until that many fights have happened

### Build
- Releases are built against the release-channel Dalamud the clients run, not staging

## v0.2.0 — 2026-08-22

### Deliveries
- The weekly roll honours the rank-5 bonus week: the request the client actually makes, and its ×1.5 scrip payout, so the overcap warning is right
- A client still ranking up is capped at three turn-ins — the request changes when the rank lands
- Gathered and fished routes gather before travelling, and the paths they fly are flown

### Gathering (experimental — debug builds only)
- Gathers delivery collectables itself: nearest live node across every spot GatherBuddy's data knows, worked with GatherBuddy's own collectable rotation (Scrutiny in front of each raise, Meticulous as raise and finisher, collect at the top band)
- GatherBuddy's node coordinates ship verbatim under Apache 2.0 — see NOTICE.md

### Allied societies
- Every society shows its full eight ranks
- A half-done day resumes from the accepted dailies instead of walking back to the issuer
- Turn-ins pick the right quest from the hand-in menu; each daily stops after itself instead of rolling into the story
- "Done" names any daily it had to drop, so a quiet failure is not called a finish
- Run buttons below the first row work again (the Deliveries and Flight windows had the same bug)

### Quest running
- Steps marked Land get off the mount before acting — flights that end above the mark no longer interact with the air
- Combat baited by an emote or a cast performs the bait first; optional combat finishes leftovers that are here and skips cleanly when none are
- Action steps wait for targets that spawn on approach; quest-item casts are no longer cancelled silently by mounting for the next leg

### Licence
- AGPL-3.0, up from LGPL-3.0: the converted quest paths are Questionable's, which is AGPL, and the licence must carry what its sources carry
- The path pack is no longer shipped — convert your installed paths locally, as before. NOTICE.md lists every source and its terms

## v0.1.0 — 2026-08-16

First cut. Everything below is built and unit-tested; in-game verification is in progress.

### Run
- Walks Main Scenario quests step by step: interact, accept, hand in, walk, fight, attune, emote, jump, use item, say, equip recommended
- Teleports and aethernet hops via Lifestream when a step names an aetheryte and you are in the wrong zone or far away
- Hands solo instances to BossMod Reborn and dungeons to Theseus; stops and names 8-player trials
- Finds the character's story frontier and offers to start it; rolls into the next quest on completion, with *Stop after this quest*, a level stop, and a clear reason when the story is blocked
- Every step has a watchdog; a stuck sequence is replayed a bounded number of times, then faults with a reason

### The Wake (resume)
- Progress is read from the game's own quest state — sequence and the six quest variables — never from a saved file; resume lands on the first step whose landmark is not yet set
- Optional confirmation before picking a quest up mid-way

### Paths
- Converts the quest paths already installed on the machine into Odysseus's own format, once; nothing is downloaded or redistributed
- Records new paths from play, including the progress landmarks resume uses
- Step editor: fix a position or id, run just that step, save

### Fleet
- Read-only dashboard over the Daedalus relay: who is where in the story, what state, last seen

### Diagnostics
- Step log with repeat offenders and a copy button; `runlog.jsonl` under the config directory
- Debug window: the story frontier's two sources and every accepted quest's live sequence and variables
