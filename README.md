# Odysseus

**Runs the Main Scenario unattended — and remembers where it stopped.**

A Dalamud plugin for FFXIV that walks the Main Scenario quest by quest: travels to the right zone,
talks to the right NPC, makes the dialogue choices, fights what needs fighting, and hands
instanced content to the plugins that already do it — BossMod Reborn for solo duties, Theseus for
dungeons.

Odysseus took the long way home. This plugin walks it the same way, and treats the wake behind
the ship as the feature that actually matters.

> **Status: pre-release, in field testing.** All subsystems are built and unit-tested (134 tests);
> the first end-to-end quest runs are being verified in-game. Expect rough edges in step timing.

## What makes it different

**It resumes — from the game, not from a file.** Quest, sequence and the quest's own progress
variables are all held server-side, so Odysseus reads them back rather than trusting anything it
saved. A crash, a logout, a plugin reload or a full client restart all pick up exactly where the
game says you are. The run window shows the live quest state and, in foam, what the Wake did
about it.

<img src="images/run.png" alt="Odysseus run window: live quest state, the Wake's resume line, and the stop button" width="440">

**It hands off instead of reimplementing.** Solo instances go to BossMod Reborn's AI. Dungeons
inside a quest go to Theseus. Combat goes to Daedalus. Odysseus owns the quest and the walk
between; everything else is delegated to the plugin built for it. Each handoff is a toggle; with
it off, Odysseus walks to the entrance and waits for you. Eight-player trials are never
automated — the run stops, names the trial, and waits.

<img src="images/settings.png" alt="Odysseus settings window: sidebar with Run, Recovery, Fleet and System sections; dependency chips in the footer" width="660">

**It knows the story.** With no MSQ quest accepted it finds the character's story frontier from
the game's own quest chain and offers to start it; when a quest completes it rolls into the next
one, stopping at a level you set or when you arm *Stop after this quest*.

**Fleet visibility, not fleet lockstep.** Every character quests independently. A read-only
dashboard shows where each box in the fleet is in the story, which state it is in, and when it
was last heard from.

<img src="images/fleet.png" alt="Odysseus fleet dashboard: one row per character with quest, sequence, state and last-seen" width="560">

**Its paths are yours.** Quest paths are converted once from data already on your machine, or
recorded from your own play — talk to NPCs, fight, teleport, and the recorder writes the steps,
including the progress landmarks resume relies on. A step editor fixes a bad position or id in
place and runs just that step to check it. A step log lists what ran, what failed, and which steps
keep failing.

> The screenshots above are design mockups of the intended UI, not captures of the running
> plugin.

## Dependencies

| Plugin | Role | Required |
|---|---|---|
| vnavmesh | pathfinding and movement | yes |
| Lifestream | aetheryte and aethernet travel | yes |
| TextAdvance | dialogue advance and cutscene skip | yes |
| Daedalus | rotation for quest combat; fleet relay | for combat / dashboard |
| BossMod Reborn | solo instanced duties | for solo duties |
| Theseus | dungeons inside quests | for dungeons |

A missing dependency is shown in the settings window and stops the run; it never crashes.

## Commands

- `/odysseus` — main window (`/od` for short)
- `/odysseus config` — settings (import paths under *Paths*)
- `/odysseus fleet` — fleet dashboard
- `/odysseus log` — step log
- `/odysseus paths` — step editor and recorder
- `/odysseus debug` — live quest-state dump
- `/odysseus stop` — stop the run

## Install

Add the aggregate repository to Dalamud's custom plugin repositories:

```
https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json
```

## Building

```
dotnet build Odysseus.sln -c Debug
dotnet test Odysseus.Tests
```

Requires the Dalamud dev libraries at `%APPDATA%\XIVLauncher\addon\Hooks\dev\`.

## Licence

Odysseus is original work. It reads quest-path data from the user's own machine at runtime and
never redistributes third-party data.
