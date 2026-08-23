# Odysseus

**Runs the Main Scenario unattended — and remembers where it stopped.**

<img src="images/icon.png" width="128" align="right" alt="Odysseus icon">

A Dalamud plugin for FFXIV that walks the Main Scenario quest by quest: travels to the right zone,
talks to the right NPC, makes the dialogue choices, fights what needs fighting, and hands solo
instances to BossMod Reborn and dungeons to [Theseus](https://github.com/ofnature/Theseus).

Progress is never kept in a file. It is read back out of the game's own quest state — quest id,
sequence, and the six server-side quest variables — so a crash, a logout or a plugin reload resumes
exactly where it stopped. That subsystem is called **the Wake**: the trail a ship leaves behind.

Beyond the story it also runs the recurring things that sit alongside it — allied society dailies,
custom deliveries, aether currents for flight, and any quest chain you point it at.

> **Status: 0.1.0, first cut.** Everything documented here is built and unit-tested (243 tests).
> In-game verification is in progress and the plugin is hidden in the repo listing until it is done.
> Treat it as something to watch rather than something to rely on.

---

## Installing

Add the third-party repository to Dalamud:

```
https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json
```

Odysseus appears in the installer once it is unhidden. Until then, clone and build:

```bash
dotnet build Odysseus.sln -c Release
```

### What it needs

| Plugin | Why | If missing |
|---|---|---|
| **vnavmesh** | walking and flying | movement stops; the step says so |
| **Lifestream** | teleports and aethernet hops | travel steps fail with the aetheryte named |
| **TextAdvance** | skipping text and cutscenes | dialogue stalls |
| **BossMod Reborn** | solo instanced duties | stops at the entrance and waits for you |
| **Theseus** | full dungeons inside a quest | stops at the entrance and waits for you |
| **Artisan** | crafting for custom deliveries | the delivery stops and says what to craft |
| **GatherBuddy Reborn** | gathering for custom deliveries | the delivery stops and says where to find it |
| **Daedalus** | combat, and the fleet dashboard relay | fights are yours; no fleet view |

Every one of them fails open. A missing plugin degrades the feature that needs it and says so in
the window — it never throws inside a run loop.

---

## The windows

`/odysseus` or `/od` opens the main window. Each panel below has its own subcommand.

### Main — `/od`

The run itself: what quest, what step, and the controls. Compact mode strips it to the top three
lines for a corner of the screen.

```
┌─ Odysseus ───────────────────────────────── ≡ ▾ ✕ ─┐
│  ● Step            HW MSQ · 41 of 138 · 3h 12m     │
│                                                     │
│  QUEST ─────────────────────────────────────────    │
│  Mogwin's Trial · sequence 1                        │
│  Interact · Moghome (2 of 3) · vars 16 16 0 0 0 32  │
│                                                     │
│  THE WAKE ──────────────────────────────────────    │
│  Resumed at step 2 from game state after reload     │
│                                                     │
│  [ ▶ Start ] [ ⏸ Pause after step ] [ ⏭ Skip ]      │
│  [ ■ Stop  ]                       [ ↻ Retry ]      │
└─────────────────────────────────────────────────────┘
```

### Journal — `/od journal`

Every quest line the game knows, grouped the way the journal groups it. **Chronicles of a New Era**
is opened first — that is where trial, raid and hard-mode unlocks live.

Queue takes a quest *and everything it needs first*. Queueing the last quest of a line is enough;
the chain is resolved backwards for you.

```
┌─ Odysseus Journal ─────────────────────────────────────────────┐
│ [Search quests and categories    ] ☑ Hide completed ☑ Only w/  │
│                                                       paths     │
│ ▼ Chronicles of a New Era  (48)                                 │
│    ▼ Alexander  (12)                                            │
│       [+ Queue]  Alexander's Heart      #3000 · Lv 60           │
│       [+ Queue]  Heart of the Creator   #3001 · Lv 70 · 5 in    │
│                                                        chain    │
│    ▶ The Warring Triad  (6)                                     │
│    ▶ Return to Ivalice  (9)                                     │
│ ▶ Sidequests  (1330)                                            │
│ ▶ Class & Job Quests  (841)                                     │
└─────────────────────────────────────────────────────────────────┘
```

### Flight — `/od flight`

Aether currents, zone by zone. Flying needs every current in a zone, and they come from two places:
quests (mostly *side* quests, which is why running the MSQ alone leaves zones grounded) and objects
lying in the world.

**Queue** puts the quest-granted ones on the priority list. **Collect** walks to the loose ones and
attunes them — in the zone you are standing in only. Positions come from the converted paths, so a
current no path ever visited is counted and shown but not guessed at.

```
┌─ Odysseus Flight ──────────────────────────────────────────────┐
│  ( Flying in 31/48 zones )   ☑ Hide finished                    │
│                                                                 │
│  Zone                        Currents  Missing        Actions   │
│  (here) The Sea of Clouds       9/15   3 quest · 3    [Queue 3] │
│                                        ground        [Collect 3]│
│  Coerthas Western Highlands    12/15   0 quest · 3    [Collect 3]│
│  The Churning Mists            15/15   flying                   │
└─────────────────────────────────────────────────────────────────┘
```

### Deliveries — `/od deliveries`

Custom deliveries: this week's bonus routes, the shared allowance, and the scrip position.

The scrip table is the point of the window. **A turn-in that would push a scrip past its cap is
refused** with the numbers, rather than quietly wasting the overflow.

```
┌─ Odysseus Deliveries ──────────────────────────────────────────────┐
│ (Unlocked 9/12) (9/12 deliveries left) (Artisan ready)              │
│ (GatherBuddy ready)  ☑ Test run — one delivery   Craft as [CUL ▾]   │
│                                                                     │
│ Client            Bonus  Deliveries  Satisfaction  Actions          │
│ [0] Zhloe Aliapoh ☐☐☐    3 / 6      rank 5        [Craft turn-in]  │
│                                                    [Gather] [Fish]  │
│ [5] Ehll Tou      ☐☑☐    0 / 6      rank 3·0/780  [Craft turn-in]  │
│ [6] Charlemend    —      —          —             [🔒 Unlock]       │
│                                                    5 quests · Lv 70 │
│                                                                     │
│ SCRIPS                                                              │
│ Currency                 Current   Cap   Max gain  Overcap          │
│ [2] Purple Crafters'       2,563  4,000     1,920      483          │
│ [6] Orange Crafters'       3,595  4,000     1,512    1,107          │
│                                                                     │
│ ▼ Spending                                                          │
│   ☐ Spend scrips automatically when a turn-in would overcap         │
│     ☑ Master recipe tomes I have not read                           │
│     ☑ Command Materia — 100   [keep 20]  ×                          │
│     [Add an item…▾]      Keep in reserve [500]                      │
│   [🛒 Spend]  2 purchase(s) for 900 Purple Crafters' Scrip.         │
└─────────────────────────────────────────────────────────────────────┘
```

### Allied Societies — `/od tribes`

The twenty societies, their rank, today's allowance, and a run button per society. Locked ones get
an **Unlock** button that queues the whole prerequisite chain.

### Others

| Command | Window |
|---|---|
| `/od config` | settings (below) |
| `/od fleet` | read-only dashboard of every character publishing over Daedalus's relay |
| `/od paths` | path browser, recorder and step editor |
| `/od log` | the run log — every step, why it stopped |
| `/od debug` | live quest state: accepted quests, sequences, the six variables |
| `/od stop` | stop whatever is running |

---

## Options

`/od config`

### Running

| Option | Default | What it does |
|---|---|---|
| **Enable Odysseus** | off | the master switch; nothing moves until this is on |
| **Continue into the next MSQ quest** | on | off = finish the current quest and stop |
| **Stop at level** | 0 (off) | park before a level cap or a duty sync level |
| **Pick quest rewards** | on | TextAdvance chooses; Odysseus presses Complete |
| **Preferred Grand Company** | — | which company the story joins when it asks |

### The Wake (resume)

| Option | Default | What it does |
|---|---|---|
| **Enable resume** | on | pick a half-finished quest back up from game state |
| **Confirm before resuming** | off | ask first instead of continuing straight away |

### Handoffs

| Option | Default | What it does |
|---|---|---|
| **Hand solo duties to BossMod Reborn** | on | off = stop at the entrance |
| **Hand dungeons to Theseus** | on | off = stop at the entrance. 8-player trials always stop |

### Priority list

| Option | Default | What it does |
|---|---|---|
| **Keep the list across sessions** | on | off = it lasts until the client closes |
| **Drop completed quests automatically** | on | prune entries the game says are done |

### Deliveries

| Option | Default | What it does |
|---|---|---|
| **Craft as** | current job | which crafter makes delivery items — Artisan switches you to it |
| **Spend scrips automatically** | off | only when a turn-in would otherwise overcap |
| **Master recipe tomes I have not read** | off | cheapest first, one copy each; stops once they are all read |
| **Keep in reserve** | 500 | scrips spending will not touch |

### Display

| Option | Default | What it does |
|---|---|---|
| **Theme** | Day | Day (light blue) or Dusk |
| **Compact mode** | off | strip the main window to three lines |
| **Publish fleet status** | on | share progress with the fleet dashboard |

---

## Quest data

Odysseus runs from its own path format. It converts the quest bundle from **your own installed copy
of Questionable**, on your machine, at import time — see `/od config → Import`.

**Nothing derived from that bundle is redistributed here.** No converted paths, no bundle, no
extracted corpus is in this repository, and none ever will be. Paths recorded with Odysseus's own
recorder are ours and ship freely.

---

## Building

```bash
dotnet build Odysseus.sln -c Debug     # what you run in-game while testing
dotnet build Odysseus.sln -c Release   # what ships
dotnet test Odysseus.Tests/Odysseus.Tests.csproj
```

Both configurations must build with zero warnings before a change is done. `Dalamud.NET.Sdk` 15.0.0,
API level 15, .NET 10.

---

## The suite

Odysseus is one of several plugins that share the Daedalus relay:

| | |
|---|---|
| [Daedalus](https://github.com/ofnature/Daedalus) | combat rotations, and the relay everything else publishes to |
| [Theseus](https://github.com/ofnature/Theseus) | dungeons |
| [Charon](https://github.com/ofnature/Charon) | collections |
| [Caduceus](https://github.com/ofnature/Caduceus) | manual mouseover healing |
| [SealBreaker](https://github.com/ofnature/SealBreaker) | FATEs |

---

## Licence

**GNU Affero General Public License v3.0** — see [COPYING](COPYING).

Odysseus was LGPL-3.0 until 2026-08-21. It is AGPL now because the quest paths it runs are converted
from [Questionable](https://github.com/PunishXIV/Questionable), which is AGPL-3.0, and a licence that
carries less than its sources cannot honour them. Everything Odysseus draws on and the terms it
draws on them under are listed in [NOTICE.md](NOTICE.md).
