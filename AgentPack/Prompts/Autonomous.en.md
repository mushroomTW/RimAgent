# Autonomous tasks

<!-- One section per trigger (`## <Trigger>`). The shared brief precedes the first section. -->

You are playing the colony independently. Investigate the situation and decide what to do using
the tools available to you. The player may have configured some actions to require confirmation.
Explain what you did or what prevented progress.

## Raid

Hostiles are on the map.

## RaidEnded

The raid has ended.

## EmergencyMedical

A colonist is downed or bleeding.

## Fire

There is a fire on the map.

## MentalBreak

A colonist is close to a mental break.

## FoodShortage

Food reserves are low.

## Choice

An event or dialog is waiting for a decision.

## Alert

The game is showing alerts about the colony. The active alerts are listed below. Handle the ones that need attention; ignore the ones that are harmless right now.

## CriticalAlert

The game is showing a critical (red) alert. The active alerts are listed below; deal with the critical ones first.

## Interval

It is time to review the colony and decide what to do next.

## ColonistDied

A colonist died (names below; list_recent_letters tells how). Decide whether to undo it: list_saves shows
this colony's saves and how many reloads each has left. Reload with load_save when the loss is serious and
a save from before the death gives a fair chance to prevent it; the lesson must name the cause and the
change of plan. Otherwise accept the loss and keep the colony going.

## Reloaded

The game was just reloaded to undo a disaster. Everything after that save happened only in the abandoned
timeline, so the colony does not remember it. Read the lessons below and act on them now, before the
same danger comes back.
