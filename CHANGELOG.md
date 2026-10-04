# Changelog

<!-- LATEST-START -->
## v0.2.14 — 2026-10-04

### Aether currents
- Flight window → "Show on map": the aether currents still to get go on the map (a whirlwind) — whichever zone's map you look at — and on the minimap, and so do the givers of the current quests taken there (the quest's "!", named). Ones on ground the story has not opened yet are labelled "later". "Collected too" shows every current, held or not (held currents labelled "collected", done quests ticked), in zones you already fly in as well
- "Flag nearest" on each zone's row puts the map flag on the nearest current still to get (or its quest's giver), ground the story has opened first, and opens the map on it
- Every loose current now has a spot: the ones no quest path walks to are read from the zone's own layout, so Collect reaches all of them and "no path recorded where" is gone (152 of 152 placed)

<!-- LATEST-END -->

## v0.2.13 — 2026-10-04

### Getting there
- Kholusia is reached from the Crystarium on the aspiring amaro tamer at the Amaro Launch ("Travel to Kholusia?"), the only way in before Kholusia's aetherytes are attuned: A Still Tide (3283) had faulted "no aetheryte there" from the Crystarium. A ride's plain "Travel to <zone>?" question is answered like a warp's. When several doors lead into a zone, the one in a zone Odysseus can reach is taken (from the Ocular it had picked one in a zone a new character cannot reach), and a door across a city is reached by its aethernet — the Amaro Launch shard, not a walk from the aetheryte plaza
- A teleport Ariadne starts mid-walk (when teleporting beats walking) is no longer cancelled by the "not getting any closer — jumping" rescue: standing still for the cast, or while Ariadne waits for a zone's mesh or plans the route, read as stuck
- Aetherytes and shards drawn on a zone's other map layers are found: Eulmore is drawn on three, and only the main one was read, so its Mainstay shard and its aetheryte were "not on any map" (A Blessed Instrument, 3289)
- "Attune this zone" takes shards first and the zone's aetheryte last, each nearest first across the ground: in Eulmore it climbed to the Canopy for the city aetheryte (story-locked, as it happens) before the shards around you. An attune that gets no closer for 20 seconds — somewhere the story has not opened yet — is given up and named instead of pacing for 90. An aetheryte or shard is found by its own object when it is in view, not by whatever stands nearest across the ground — Eulmore's Mainstay shard, right under the city aetheryte, had been taken for it and walked back down to. One the game will not attune — story-locked, like Eulmore's early in Shadowbringers — is given up on the game's own "special permission is required" (or after five presses at arm's length) and left out of the button's count and attuning on the way — remembered per character until the story has moved on; one it cannot reach is given up after 20 seconds of standing still (walking the long way round counts as progress) and tried again next time
- Gates inside a zone are used, and a path's own step to a gate guard walks to him (it had read "you are already in this zone" as "already crossed" and stood 550 y away): a walk that finds no way across a wall takes the nearest gate the path library knows — the Peaks' Ala Mhigan Resistance gate guards, which Closing Up Shop's first steps sit behind — then walks on
- Going through a door, riding a lift or attuning waits while pathing reloads instead of running out its clock: an Ariadne update mid-run left the character standing still and the trip to the amaro faulted after a minute
- A fight the step never asked for is waited out instead of faulting — on the way, at an attune, a door, a lift or an interact: a mob that attacks mid-walk had Daedalus fighting and Minerva dodging, every walk Odysseus issued was cut off, and the step faulted "no path" with a good route (A Still Tide), and a hunt mark that wandered into an attune ran its clock out. The step now holds, gets off the mount so Daedalus can fight, and when the fight ends mounts back up (if the fight took it off, or the rest is a ride worth taking) and goes on. With Daedalus switched off it walks on as before

### Dialogue
- A step that names several menu answers gives each menu the one it offers: A Taste of Honey (3288) answered its high-or-low menu with "Let's play!" (the first answer named), which that menu does not have, and guessed the first option every round

### Aether currents
- Settings → "Pick up aether currents while doing the story" (off by default): between quests, in a zone you cannot fly in yet, its aether current quests that can be taken now go on the priority list and run before the story continues, and its loose currents are collected once per zone per session. Current quests already in your journal are queued too, wherever they are: the Shadowbringers story paths pick up every one they pass (City of Final Pleasures takes A Plankless Task and Village of Woe) and never came back for them. One that fails — somewhere the story has not opened yet, as parts of Kholusia stay shut for a while — is set aside and the story goes on; it is tried again once an MSQ quest has been completed (side quests finishing do not count — that had looped Closing Up Shop). Loose currents on the side of a zone the story has not opened yet (Shadowbringers zones are split that way) are left for later and counted, judged by where the MSQ paths go and which of those quests are done. A stop asked for (Stop after, one quest at a time, the level stop) still stops at the quest's end, with no detour first. Collected currents are pressed by their own object (the collector had walked to each spot with nothing to press, and Lakeland went 0 of 2), and one that will not attune is named at the end instead of ending the round

### Settings
- General is grouped under Story, Dialogue and rewards, Travel, Combat, and Gathering and crafting. The Handoffs page is folded in: the duty handoffs (solo duties, duty AI, Minerva preset, Theseus) under Combat, the crafter choice (Artisan or Hephaestus) under Gathering and crafting

### Paths
- Hand fixes to quest paths now ship: they live in the plugin's Assets/PathFixes and are laid over the shipped library and any import, on every install. First one: A Taste of Honey (3288), up Eulmore's spiral staircase on the mesh instead of the old no-mesh steps
- Settings → Paths → "Save path edits to": a central fixes folder. Path Editor saves go there, and every install pointed at it runs those fixes after a reload

## v0.2.12 — 2026-09-30

### Getting there
- Fastest way first: where flying is unlocked every mounted leg flies (not only the ones the path marks or the very long ones); where it is not, the mount; where no mount is allowed, Sprint on any leg over 25 yalms
- Across a city with no aethernet shortcut in the path, Odysseus hops to the shard nearest the mark when that beats walking by a clear margin — the Disciple of the Hand quests walked Ul'dah end to end one way and hopped it back
- No more hopping through town: the "not getting any closer — jumping" rescue now needs the character to be genuinely stuck (barely moved in four seconds), so a route round a building or back to an overshot waypoint is no longer a jump at every bend, and it never jumps in a zone that allows no mount
- A mark on another level of a city takes the aethernet hop the path names instead of walking: Ul'dah's top level read as 148 yalms away and walkable, and the walk went to the lift rather than the Airship Landing shard beside the NPC (A Sultana's Duty, It Could Happen to You)
- A door's travel question — "Enter the Ruby Bazaar offices?" — is answered whatever the step is doing, walking up or interacting, and for the step's own zone even when the path marks no crossing; the question is read from the window's own values when its text node is empty, which left some of them unrecognised and waiting for a click
- Zones with no aetheryte of their own are entered by their door: the Rising Stones from Mor Dhona, and the 300-odd other doors the path library walks through (inn rooms, Grand Company offices, …). Prelude in Violet (3149) had faulted "no aetheryte there" with the character standing outside. Starting from elsewhere, Odysseus teleports to the door's zone first
- City lifts are ridden. An Airship Landing has no shard of its own and only joins the aethernet once every shard in the city is attuned; until then the walk up ended at the lift doors. Odysseus now goes to the lift attendant (Willahelm, Nanahomi, Lolomaya in Ul'dah; Blanmhas, Skaenrael, Grehfarr in Limsa), picks the stop, and walks the rest
- The lift menu is answered: it is the icon menu, not the plain one, so a path's choice for it was never picked and a path that talks to the attendant without naming a stop (When the Dust Settles, 4063, riding down from the Airship Landing) sat with the menu open. The stop is now the path's choice, or the one in the zone the step crosses into
- (Ariadne) a flight answered with a ground route ("groundFaster") is ridden as a ground route — landing first if already in the air — instead of being steered as a flight, which hopped along the ground waypoints and overshot the mark

### Aetherytes
- A shard on a different level than its map marker suggests is still reached: the marker has no height, the guess put the Crystarium's Cabinet of Curiosity on the level above, and "Attune this zone" gave it up as no path every time with the shard right below. Odysseus now goes to the shard itself as soon as it is in view, matching it across the ground rather than by the guessed height
- Attune steps attune: the converter kept no name for the aetheryte or shard, so every one of the path library's 319 attune steps finished in a frame without attuning anything — the Doman Enclave was never attuned, and its teleport was refused a quest later. They now walk to it (its spot from the city or zone map), interact until the game says it is attuned, and close the menu it opens. The path library is rebuilt as format 7 to carry the names
- On the way through a quest, an aetheryte or shard within 35 yalms that is not attuned is attuned first, then the step carries on
- "Attune this zone" — the gem button on the main window's icon row (also in the Flight window, which Quick access now opens): every aetheryte and shard in the zone you stand in that this character has not attuned, one after another; any it could not reach is named at the end
- An aethernet hop to a shard in the same zone that is not attuned walks instead, and a path's teleport to an aetheryte this character has not attuned finds another way rather than being refused for thirteen seconds

### Quests
- A step that goes through a door into another zone is done when the character is already on the other side: Gosetsu and Tsuyu (3070) opens with Kugane's guard into the Ruby Bazaar offices, the quest before it ends inside them, and the aethernet hop asked for from the offices ended in Lifestream's "Destination could not be found"
- Sniping sections (Securing the Saltery and 31 others, Stormblood onward) run instead of stopping with "Snipe is not implemented": Odysseus goes to the rifle, interacts with it, and the shots are skipped — CBT's "Sniper no sniping" is built in (ported, BSD-3-Clause), switched on for the section only, so CBT need not be installed. If a patch moves the game function it hooks, you are told once to take the shots yourself, and the run waits however long that takes

### Materials
- "Grab from FC" takes exactly what is short: a stack holding more is split through the game's own "how many?" prompt, so 6 Copper Ingot out of a stack of 99 brings 6 and leaves 93 in the chest for the other characters. It used to bring the whole stack

### Beastmaster
- The Wilds Call (5491) runs on its own: its "assign Cu Sith to the first battlehorn, summon, then talk to J'yhuh Tia" step was a manual one. Odysseus opens the Master's Bestiary (closing it again after), puts the pet on the battlehorn unless it is there already, summons it, and talks. Any step written "assign <pet> to <first|second|third> battlehorn" is read the same way

### Tools
- `/od battlehorn 1 Cu Sith` puts a Bestiary pet on a battlehorn (opening the Master's Bestiary if it is not up); `/od battlehorn` lists what is on each one

## v0.2.11 — 2026-09-27

Hephaestus can do the crafting, quests that want HQ or melded items get them, and gather lists handle timed nodes.

### Crafting
- Settings → Handoffs → "Crafting" picks who makes items for Craft steps and deliveries: Artisan (as before) or Hephaestus. The switch is immediate; a craft already under way finishes with the crafter that started it, and every status line and stop names whichever is in use

### Crafting (quests)
- A Craft step that names no item makes what the quest hands in instead of stopping with "Craft step names no item": the quest's own hand-in items that have a recipe, each one not already in the bag, in turn, as many of each as the path's own note says (To Be the Wood takes three shields, Supplies for the Sick twelve lumber; the path library is rebuilt as format 6 to carry the note). Ten upstream quests have such a step — My First Saw (205, Maple Lumber), To Be the Wood (139), A Carpenter in Need (141, a harpoon and a shortbow), and the Ishgard culinarian quests among them
- A craft the quest takes only at high quality is held to it: the path's note marks those items HQ (56 quests — A Crisis of Confidence's Walnut Lumber, Might Made Right's Hi-Potions of Strength, the Ishgard culinarian dishes), only HQ copies count toward the target, and a normal-quality result is crafted again. Two normal-quality results in a row stop the step with a reason rather than burn more materials
- A craft the quest takes only with materia melded in is melded: the path's note names the materia ("Crab Bow HQ with 1x Savage Aim Materia III", "any Materia", "any grade I"), and once the item is made Odysseus opens Materia Melding, picks the item and a materia from the bags that fits — the lowest grade that does, so "any Materia" spends the cheapest — and presses Meld only when the confirmation names that item and that materia (The Lance's Lesson, Saving Captain Gairhard and fourteen others). No fitting materia, melding not yet learned, or a confirmation that names anything else stops the step with the reason and melds nothing
- A Craft step's "already in the bag" skip counts only what the quest will take — HQ when the note says HQ, melded when it asks for materia — so an unmelded or normal-quality copy no longer skips the craft, the check or the meld
- `/od record <Window>` writes to the log what a window sends the game when you click in it; again to stop
- A craft that stopped with its materials all there says which crafter to check instead of printing its name placeholder

### Getting there
- An aethernet hop asked for with no shard in view walks toward the nearest shard the city map shows, then to the shard itself once it loads, then hops. Blood Ties (2617) ended a step at the far end of Limsa's Upper Decks and asked for the hop from there; Lifestream went nowhere and the step faulted after 90 seconds

### Gather lists
- An item only timed nodes yield shows when it is up, counted down in real time beside its name: "up · 4:12 left" in green, "in 12:34" in yellow — from the node's own window in the game's data (Grade 3 Shroud Topsoil: 06:00 for three Eorzean hours)
- A run gathers timed items too: the rest of the list first, then "Waiting for … — its node is up in 12:34", and off the moment it opens (with a minute of the window left, at least). A window that closes mid-gather puts the item back for the next one, three windows at most. The gathering gate other plugins call still promises only always-there nodes
- A timed node opening while an ordinary item is being gathered: the node in hand is finished, the timed one gathered while it is up, then the ordinary item is taken up again
- A timed item worked this window waits for the next one, even after Stop and Gather again — it no longer chains round its empty spots — and a timed node not up at any of its spots is looked for once round, not twice
- When the item wanted is gone from a node, the rest of the node goes on Dark Matter Cluster if it offers one — an unspoiled node gives its item and keeps its attempts
- "Gather" runs the list on screen and nothing else; "All enabled" runs every list with Enabled ticked, as "Gather" used to. A crystals list left enabled was gathered alongside a list that only asked for yew branches

### Materials
- A crafted item the FC chest already holds is fetched, not made: its ingredients leave the materials list, so "Grab from FC" takes the item instead of the item and everything to craft it. Only what the chest cannot cover is still expanded into ingredients

### Dialogue
- An NPC's chat menu that the path names no answer for is no longer asked about forever: the first line is still tried once, and when the same menu comes back the last line ("Nothing.") ends the talk. Straight after a hand-in Severian answered Might Made Right (648) with his chat menu, and Odysseus asked "What do you do here?" every three seconds for minutes; now it leaves the conversation and asks him again, which is when he offers the quest

## v0.2.10 — 2026-09-27

Dives stop sinking, area crossings answer their own travel question, and item fights hold Daedalus from the start of the step.

### Getting there
- A dive lets go of the Descend key once under water: If I Were a Fish (2881) and every other dive step sank without end until someone pressed the key by hand
- A step that crosses into another area answers the game's own travel question for it — the gate guard's "Leave the Ala Mhigan Quarter?" in The Mad King's Trove (2964) waited for a click. Only the questions the game lists for travel into the step's destination are answered; any other yes/no still waits for you

### Quest fights
- A fight that wants the mob kept alive under a health line holds Daedalus from the start of the step, not from the start of the fight: a mob that aggroed on the approach was Daedalus's to kill before the item could go on (Are They Ill-tempered, 2883)
- The log follows the mob's health on the way down, says when Daedalus did not take the hold, and says where the mob was last seen when a fight ends without the item

## v0.2.9 — 2026-09-26

Quests that want an item used on a weakened mob now use it, the shipped path library is rebuilt with 84 more quests, and a toon with Daedalus switched off keeps its target.

### Quest fights
- A combat step no longer pulls while Daedalus is switched off: nobody would fight, and every pull re-targeted the nearest mob, so a toon with Daedalus off could not keep anything targeted. The step waits, says so once, and carries on when Daedalus is turned back on (needs Daedalus v0.1.87)
- Quests that want an item used on a weakened mob use it: 34 quests, among them Are They Ill-tempered (2883). For "below X% health", Daedalus is held for the fight so auto-attack brings the mob down slowly and the item goes on under the line; for a mob down on one knee, Daedalus fights normally and pauses only while the item goes on. Without Daedalus v0.1.87 the item still goes on when the mob is ready, but Daedalus may kill it first

### Path library
- Rebuilt from Questionable bundle 1789898831 (2026-09-20): 4,324 quests, 84 more, including full paths for Strangers in the Wood (5490) and The Wilds Call (5491). Paths you imported with an older build step aside for the shipped copy — no re-import needed
- Rock the Castrum (3873) gained a new step upstream that asks to clear other quests from the journal. Odysseus passes it and leaves your journal alone instead of stopping the main story there

## v0.2.8 — 2026-09-26

TextAdvance no longer blocks Start, quests nobody recorded can run, and a run that meets something it cannot do stays where it is and says so instead of wandering off.

### Required plugins
- TextAdvance is optional: Start needs only a pathing plugin and Lifestream. Without TextAdvance the main window says what is slower — cutscenes play in full, and a quest offering a choice of rewards waits at the window for you to pick

### Quests with no path
- A quest with no recorded path is run from the game's own journal data — where the giver stands, where each objective points, who takes it back — walking, talking and interacting, and stopping with a reason if the quest wants anything else. Settings → "Run quests with no path from the game's own journal data"; the quest line says *(derived path)* while one is in use
- A derived walk to a quest-map search area arrives anywhere near its middle instead of faulting yards from a centre the mesh cannot reach
- The main window's "What the game says" section shows the journal's own objective positions beside the path's
- A priority row says "no path" instead of a sentence cut off mid-word

### Getting there
- A step already standing on its own mark does not travel to it: the recorded teleport to a neighbouring zone's aetheryte no longer throws the run out of the zone it is already in
- A route is decided once the game has stopped moving the character: a quest cutscene that carries you to the next NPC no longer ends with a teleport away from them and a run back
- Side quests a story path picks up on the way are taken by name from the NPC's quest menu, skipped once already taken, and the walk to them is skipped when nothing is left to pick up
- Settings → "Skip optional side-quest pick-ups" leaves them — and the detour — unless the story or the priority list needs one
- A Duty Finder duty does not travel to where it was recorded; the aetheryte list is warmed before the first teleport

### Duties
- A lost solo duty is not called done: the run stays put, says so once in chat, and carries on when you win it — it no longer replays the sequence from the top and walks off
- Quest battles the path data marks as not runnable unattended wait at the entrance without going in
- A duty Odysseus does not run (raids, trials) waits for you instead of faulting
- Settings → "Duty AI": BossMod Reborn or Minerva. Minerva is claimed for the fight as "odysseus" and handed back after, with an optional preset
- "Duty calls — begin?" is answered on a solo-duty step

### Dialogue
- A yes/no is answered only when it matches the question the path recorded, looked up across the whole sequence, not just the running step
- A yes/no nobody recorded is repeated to you in chat and waits, rather than timing the step out
- A path note is told to you in chat and holds the run until you have done it
- An ability still cooling down is waited for, not called refused

### Levels
- Level gates read your real level, not the synced one — a level-100 character in Bozja is no longer told they are 80

### Gathering
- Worn gear is repaired between items, and a full bag ends the run with the reason
- A node that comes back after depleting is worked again where it stands
- "+ Preset" makes a list of the elemental shards, crystals, clusters, or all eighteen
- The item search shows what is not on the list yet, plainest names first — Water and Wind Shard were unreachable once four shards were listed
- A gatherable item means one this character can actually gather: timed nodes are skipped, and Miner or Botanist must be unlocked and high enough

### For other plugins
- `Odysseus.Gather.*` gates let another plugin ask for gathering: `CanGather`, `Start`, `Stop`, `IsRunning`, `GetStatusJson`. Each caller gets its own list in the gather window, reused and pruned as it goes, and a request never touches your own lists
- Recipe ids are carried whole; Artisan's narrower gate refuses rather than crafting the wrong item

### Paths and the editor
- Strangers in the Wood (5490) ships in the library, recorded the day it released
- A recorded teleport is never written as a zone-line walk; a step can be moved between sequences; the editor can name a SwitchClass step's class and an Action step's ability
- An emote keeps one slash however it was written; stopping a recording no longer throws; the edit button opens the editor for a quest with no path, where the recorder is
- `/od values <Window>` writes what a game window carries to the log, without needing to see it

## v0.2.7 — 2026-09-06

Gather lists, and the gatherer that runs them, field-proven on a crystals list end to end.

### Gather lists
- Several named lists, each item with a target bag count (GatherBuddy's shape), in a window of their own toggled from the main window: pick, rename, enable, remove-completed, live held/target per row, an item search over every gatherable, Gather / Stop, and an Afterwards choice — Return, the inn, or an estate through Lifestream — sent once a finished run can act
- A run takes every enabled list's short items zone by zone through the own gatherer, with an outcome per item; an item that cannot be placed or that faults is recorded and the next is tried
- The own gatherer leaves debug builds for good: on by default behind Settings → "Gather with Odysseus", the Workbench's probe and dry-run modes kept as diagnostics; quest gathers, deliveries and the lists all fall back to the GatherBuddy handoff when it is off

### The gatherer
- Plain nodes: the row is pressed again once its gathering action has played, the window wait counts from the last press, and nothing is closed while an action runs — the walk-away that locks the client
- The crystal container is counted, so a shard list stops at its target instead of gathering forever
- A node reached in the air is landed by the executor's own dismount, with its stalled-descent reroute (five seconds now, onto the nearest floor the mesh knows), and the runner waits for the ground before it opens

### Getting there
- A fly move needs the saddle: on foot the leg walks, long legs mount first; the climb detour goes through the mount too
- A teleport Lifestream accepts but never starts is asked again after four seconds, and the game's aetheryte list is refreshed before every request — the first teleport of a run was being silently dropped
- Pathing can go through Ariadne instead of vnavmesh (Settings → Pathing): the same gates under its own name, switched live; it needs Mnemosyne running

## v0.2.6 — 2026-09-06

The tail of the Arkasodara chain, and the thread on the ground.

### Getting there
- A passenger ride keeps its wheels on the ground: riding a quest cart or a shared mount stands down every air rung and the in-zone teleport — no wings, no dismount, no crystal that would separate you from it
- A mount that never comes (a quest section that forbids it) kills the fly intents instead of pressing fly-on-foot into a hillside; three stall-hops without ten yalms of progress stop the leg and hand it to the re-path ladder rather than hopping a fourth time
- The route to the active step is drawn in the world — vnavmesh's live waypoints while a path is followed, else a straight line to the mark — with a ring and the distance at the goal; a config toggle turns it off

### Packaging
- NOTICE.md ships beside the Apache licence it explains, so the GatherBuddy node atlas travels with its attribution and modification statement, not just the licence text

## v0.2.5 — 2026-08-23

The Qitari and Arkasodara unlock chains, run live end to end. Every change came from a field failure and carries a test.

### Class gates
- Three gates now, each waiting for the equip to actually take before the step moves (the game silently drops a gearset change raced against a mount): Hand-or-Land, combat (best set by level), and Land-only — the gatherer, specifically, with the level judged against the gearset rather than the class you stood there as

### Getting there
- The move budget measures progress, not wall time: every ten yalms gained buys the clock back, so a long crossing is never guillotined mid-stride
- A long leg flies even when the path says walk — six yalms a second on the ground against twenty in the air makes anything past 120 worth a take-off — and a far mark with an attuned aetheryte beside it teleports instead
- A wedged walk takes to the air (mounting first), a wedged escape flight climbs over the mark and comes down on it vertically, and a combat mark the mesh cannot serve is near enough to fight from once the air has had its turn
- A wedge once solved teaches the run which way works: the next visit to that spot goes straight to the winning rung
- BossMod's AI movement controller no longer fights vnavmesh for the character: travel commands it off, fights and duties command it back on
- A refused teleport is asked again for twelve seconds before it is believed; readiness clocks hold while a cutscene plays; Yedlihmad's meshless doorway is crossed directly by the nine paths that use it

### Quests
- WaitForNpcAtPosition is implemented — escort steps hold until the NPC stands on their spot
- Quest gathers are done by Odysseus itself when the own gatherer is switched on: plain nodes worked by the row, quest-hidden items resolved through their own sheet, unplaced points borrowing the step's zone — and a decline names exactly which link is missing before handing to GatherBuddy


## v0.2.4 — 2026-08-23

An evening of field-hardening across three allied societies and the whole eleven-quest Namazu unlock chain. Every change below came from a live failure, and each carries a test.

### Getting there — the remedy lattice
- An interact the ground cannot serve flies to the object itself and lands on its floor; standing under an object's ledge no longer counts as arrival
- A flight hanging over a mark lands on it; a leg that gives up in flight lands and retries on foot (only within the last stretch); a descent with no floor flies over its goal and comes down there
- A wedged flight retries on the ground, a wedged ground leg goes back to the air, and six fruitless re-paths fault honestly with the spot named — no more sputtering until luck intervenes
- "Moving" with the position frozen for twelve seconds is stopped and re-pathed; the stall hop no longer resets that clock, never fires mid-air, and never in close quarters to an interact target
- An object step that gives up near its mark hands the last stretch to the interact instead of rebuilding the mesh; a dismount waits a beat before the first press

### Dialogue — nothing left hanging
- Quest offers are accepted by their own button; subtitle boxes advanced and cutscene skips confirmed by our own tick (no TextAdvance needed; its cutscene-ESC and reward picking remain external by choice)
- Open conversations, hand-over windows and declared choices are joined from any phase — the previous hand-in's chain, the overcap warning, the Mol Guide's ascend prompt
- The in-conversation choice window's first string is its prompt, not an option: every such answer was off by one
- The hand-over fill actually fills now (the slot picker and its menu, the proven way), and request item names read cleanly

### Combat
- Named targets are hunted ninety yalms out whatever spawned them; a fight is entered on foot, landing fifteen yalms from the mark; an arrival-spawn that stays quiet gets the last steps onto the exact trigger

### Quests
- Dive is implemented — the game's own descent bind, pressed the way it expects (ported from Questionable, AGPL into AGPL); underwater movement is volume movement
- Gathers whose crafts are already made are skipped, including raws no recipe connects once the block's crafting stands done — the crate-supplied quest completes itself
- The main window is cards now, with the priority queue in it: order, ready-states, reorder and remove on the row, add-current

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
