# Changelog

<!-- LATEST-START -->
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
<!-- LATEST-END -->

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
