# Notes for yuan-ti from the Obsidian Edition project

These notes are for the yuan-ti engine (github.com/CalvinRodo/yuan-ti) and for
whoever works on it, people or AI agents. They come from **Obsidian Edition**
(github.com/daaki85/darksun-obsidian-edition), a mod for Dark Sun: Shattered
Lands. It runs the original DSUN.EXE in DOSBox, patches it in memory, and logs
every die the game rolls.

Its findings come from four sources:

- reading DSUN.EXE's code;
- reading the running game's memory;
- a dice log that hooks every call to the game's random number routine;
- playing the game to check each result.

Where something was confirmed in play, these notes say so.

**Use.** These notes describe facts about the original game. They are offered
for free use in yuan-ti, in its documentation or its code. They contain no
code from Obsidian Edition (which is GPL-3.0) and nothing from the game's own
files.

Written 20 October 2026, against yuan-ti commit 9c1952b ("feat: more combat
features").

---

## 0. How to read these notes

### Addresses

Obsidian Edition gives addresses as **file offsets in DSUN.EXE**, from the GOG
release, which is 611,408 bytes. yuan-ti gives them as **EXE segment:offset**
in the image that `src/ovl_unpack.py` flattens. These notes give both, as
`80F5:1999 (7A539h)`.

The conversion used yuan-ti's own `ovl_unpack.py`:

- **Resident code:** subtract the MZ header, then find the containing segment
  from the Borland segment table.
- **Overlay code:** subtract the FBOV block's start + 0x10, find the overlay
  whose `fileoff` range holds it, and use the segment `ovl_unpack` gives that
  overlay.

These pairs check the conversion. Each was found by both projects on its own:

| yuan-ti | DSUN.EXE offset | What both projects call it |
|---|---|---|
| `0980:5814` | 14414h | the screen flip (Obsidian Edition's own docs give both forms) |
| `80F5:1999` | 7A539h | the routine that lands a spell or blow on its target (yuan-ti: "Armor spends a charge…") |
| `612E:1B7A` | 5883Ah | the attack roll |
| `6798:0B32` | 5E4C2h | spell slots per class and level (yuan-ti's `SpellSlotRow`) |
| `612E:1FCF` | 58C8Fh | finding a missile weapon's ammunition |
| `612E:084E` | 5750Eh | the round's initiative base of 20 being added |
| `612E:1359` | 58019h | the melee attack routine (yuan-ti: "Who hits") |

A short script that does the conversion with yuan-ti's code:

```python
"""conv.py DSUN.EXE OFFSET... : DSUN.EXE file offsets -> yuan-ti EXE seg:off."""
import sys, struct
sys.path.insert(0, "src")               # yuan-ti's src/
from ovl_unpack import unpack, segment_table
exe = open(sys.argv[1], "rb").read()
_, last, pages, nrel, hdr_par = struct.unpack_from("<5H", exe, 0)
img_end = (pages - 1) * 512 + last if last else pages * 512
hdr = hdr_par * 16
segtab = segment_table(exe, img_end)
_, ovls = unpack(exe)
codesegs = sorted({s for s, mx, fl, mn in segtab if fl in (0, 1, 4)})
def conv(f):
    if hdr <= f < img_end:
        lin = f - hdr
        seg = max(s for s in codesegs if s * 16 <= lin)
        return f"{seg:04X}:{lin - seg * 16:04X}"
    o = f - img_end - 0x10
    for stub, new, off, size, _ in ovls:
        if off <= o < off + size:
            return f"{new:04X}:{o - off:04X}"
    return "?"
for a in sys.argv[2:]:
    print(a, conv(int(a.rstrip("hH"), 16)))
```

### Numbers

- **Spells:** Obsidian Edition numbers them from 0, and yuan-ti's `SPIN` and
  record numbers are **one higher**. For example, the preserver's Dispel Magic
  is spell 26 in Obsidian Edition and record 27 in yuan-ti. These notes use
  yuan-ti's numbers.
- **Effects and conditions** appear to share numbers in both (Armor is 2 in
  both).
- **Data segment offsets** (`DS:xxxx`) are the same in both.

---

## 1. Where yuan-ti's combat differs from our findings (check these first)

### 1.1 Which way a target faces

**Obsidian Edition:**

- A creature faces no particular way at the start of each round.
- The **first attack on it in a round turns the target to face that
  attacker** (one of eight directions), and it keeps facing that way for the
  rest of the round.
- Its own attacks don't turn it.
- So a second attacker on the far side, later in the same round, attacks from
  behind: +2 to hit, and the target loses its DEX bonus and its shield.

This is from the game's code and was seen in play: the dice log reads the
attack's "from behind" and "backstab" flags. These are the attack frame's
`[BP+20h]` and `[BP+1Eh]`, pushed near `612E:16CA` (5838Ah). We did not record
the address of the code that turns the target.

**yuan-ti:**

- `Battle.Attack` turns the attacker to face its target
  (`a.Facing = Direction(a.Cell, t.Cell)`, `Battle.Attacks.cs:183`).
- `Step` turns a mover to face the way it walks.
- Nothing turns the target.

So two attackers on opposite sides don't produce an attack from behind.

### 1.2 The backstab multiplies only the first attack of the round

**Obsidian Edition:** the damage routine multiplies a backstab's damage, STR
bonus included, only when the attacks already made this round are 1 or fewer:
the attack frame's `[BP-0Ah]` ≤ 1. The multiplier is ×2 at thief levels 1–4,
×3 at 5–8, ×4 at 9–12 and ×5 from 13 (as `612E:2904`).

**yuan-ti:** `Swing` multiplies every swing that `Backstabs()`
(`Battle.Attacks.cs:253`), so a thief with 2 attacks a round backstabs twice.

### 1.3 Hold Undead holds the living too

In the original, the routine that lands a spell asks whether the target is
undead (`80F5:3280`, 7BE20h). If it isn't, the routine goes on
(`or al,al / je` at `80F5:1793`, 7A333h, to `80F5:17B9`, 7A359h) for **any**
spell, so Hold Undead (record 32) held living creatures too.

Obsidian Edition patches the jump so that a
living target of record 32 takes the immunity's way out (`80F5:1650`, 7A1F0h).

yuan-ti's whom-table (`Battle.Effects.cs`, `32 or TurnUndead => t.IsUndead`)
makes Hold Undead undead-only. That is the fixed behaviour, so for faithful
play it belongs under `CombatFixes` (off by default).

### 1.4 Two weapons: a bonus at low DEX, and no other penalty

With two weapons ready in melee, the game adjusts **every** attack of both
hands by the DEX reaction table (`6868:033F`) with its sign flipped and never
below 0. Rangers are left out (the ranger test is at `612E:295F`, 5961Fh).
That makes a **bonus**:

| DEX | 1 | 2 | 3 | 4 | 5 | 6+ |
|---|---|---|---|---|---|---|
| Bonus | +6 | +4 | +3 | +2 | +1 | 0 |

There is no off-hand penalty. Each weapon also attacks at the character's
full rate, so a 3/2 fighter's off hand attacks 1, then 2.

This was confirmed in play: the dice log shows `+2 two weapons at DEX 4`.

yuan-ti's reference describes it ("less a second weapon's penalty
(`612E:292C`: the Dexterity reaction, not for rangers)"), but the port's
to-hit (`Battle.Attacks.cs:151`) doesn't apply it yet.

### 1.5 Bugs the port reproduces or avoids

The port appears to follow the original on these. Both are candidates for
`CombatFixes`:

- **Armor** spends two charges per damaging hit: one at `80F5:1999` and one in
  the damage routine at `80F5:0E9E` (79A3Eh).
- **The preserver's Dispel Magic** (record 27) carries the No Spells
  condition.

See section 5 for both.

The port avoids one original bug: **Share Strength** ending
(`Battle.Effects.cs` ~650 removes the receiver's Borrowed). That is correct,
but not what DSUN.EXE does (also section 5).

---

## 2. The original save format

yuan-ti's roadmap §9 says original save games need reverse engineering. This
much is mapped and checked against saves of three parties.

A save file, `SAVEnn.SAV`, is a GFF. Two of its chunks hold the party:

- **`SAVE` chunk 5:** the creature table, 58-byte (0x3A) records for everyone
  in the current region, party first.
- **`SAVE` chunk 6:** the character sheets, 71-byte (0x47) records.

The same records are in memory:

- `DS:1665` is a far pointer to the creature table.
- `DS:1661` is a far pointer to the sheets.
- A creature's sheet number is its word at `+0x04`.

| Record | Offset | Type | Field |
|---|---|---|---|
| creature | +0x00 | s16 | current HP |
| creature | +0x02 | s16 | current PSP |
| creature | +0x04 | u16 | its sheet's number |
| creature | +0x06 | u16 | entity ID (0x80nn for the party), the same as the sheet's +0x10 |
| creature | +0x08 | u16 | an item list (object number of its first item) |
| creature | +0x0A | u16 | worn and held items (list) |
| creature | +0x0C | u16 | backpack items (list) |
| creature | +0x1A | s8 | base AC before armour and DEX (10; 5 for a thri-kreen) |
| creature | +0x1B | u8 | Move (movement points a round = Move × 10) |
| creature | +0x1F | u8 | THAC0 |
| creature | +0x22 | u8 ×6 | STR DEX CON INT WIS CHA |
| creature | +0x28 | char[18] | name |
| sheet | +0x00 | u32 | XP |
| sheet | +0x04 | u32 | for monsters, the XP they're worth |
| sheet | +0x08 | s16 | max HP |
| sheet | +0x0A | s16 | hit points rolled for every class level, added up |
| sheet | +0x0C | s16 | max PSP |
| sheet | +0x10 | u16 | entity ID |
| sheet | +0x12 | u16 | class bits (0x200 ranger, 0x400 thief, …) |
| sheet | +0x18 | u8 | race: 1 human, 2 dwarf, 3 elf, 4 half-elf, 5 half-giant, 6 halfling, 7 mul, 8 thri-kreen (9 undead, 14 summoned) |
| sheet | +0x19 | u8 | gender: 1 male, 2 female |
| sheet | +0x1A | u8 | alignment: 1 LG … 9 CE (rows LG LN LE, NG TN NE, CG CN CE) |
| sheet | +0x1B | u8 ×6 | STR DEX CON INT WIS CHA |
| sheet | +0x21 | u8 ×3 | classes: 1–4 cleric, 5–8 druid, 9 fighter, 10 gladiator, 11 preserver, 12 psionicist, 13–16 ranger, 17 thief (0 none); each group of four is air, earth, fire, water |
| sheet | +0x24 | u8 ×3 | the level in each class |
| sheet | +0x27 | s8 | base AC for the AC calculation |
| sheet | +0x29 | u8 | magic resistance (%) |
| sheet | +0x2A | u8 | attacks per round × 2 |
| sheet | +0x37 | u8 ×5 | saves: paralysis/poison/death, rod/staff/wand, petrification/polymorph, breath, spell |
| sheet | +0x43 | u8 | **unused by the game** (Obsidian Edition keeps its kit byte here, so a save from the mod may have it set) |

**Items.** Items are 21-byte records (`DS:165D`). Item types are 20-byte
records (`DS:1669`, 115 of them, from GPLDATA.GFF). A creature's lists are
object numbers: the object table holds a kind (1 item, 2 creature) and an
index. Each item record names the next item in its list.

| Record | Offset | Field |
|---|---|---|
| item | +0x04 | next item in the list (9999 = end) |
| item | +0x08 | a container's contents (an object number), as in a dead body |
| item | +0x0A | its type |
| item | +0x0F | the spell it carries, one past the spell's number (0 = none); also read as its "magical power" by the acid check |
| item | +0x11 | where it's worn: 0–13 the slots (arm, ammo, missile, right hand, finger, waist, legs, head, neck, chest, left hand, finger, cloak, foot), 14–25 a backpack cell |
| item | +0x12 | its name (an entry of the 25-byte name table) |
| item | +0x14 | its plus |
| type | +0x04 | weight, in tenths of a pound |
| type | +0x08 | material in the low 4 bits; 0x80 spares a weapon its material's to-hit penalty |
| type | +0x0C, +0x0D, +0x0E | damage dice: sides, count, bonus |
| type | +0x0F | 0x80: counts for AC; 0x40: two-handed |
| type | +0x12 | its AC (on top of the item's plus) |

**A quirk to keep:** the game fights with every weapon in the creature's
worn list (+0x0A), whatever the item's slot byte says. A dagger whose slot
byte is a backpack cell, but which is still in the worn list, is a weapon in
hand: the inventory lists it and the character attacks with it.

**A quirk of the game's own party:** its four characters start with their
worn armour in the backpack list (+0x0C), with worn slot numbers.

---

## 3. Character rules: THAC0, level-ups, XP, slots, creation

yuan-ti's roadmap says "Armour class and THAC0 follow AD&D 2nd edition
(DSUN.EXE's formulas aren't traced)". These are traced.

### THAC0

The routine is `8E77:0416` (87666h). THAC0 = 20 − the most, over the
character's class groups, of (level − 1) × factor ÷ 12. The factors are:

- priests 8;
- warriors 12;
- wizards 4;
- rogues and psionicists 6.

Only the priests' differs from AD&D: 19 at 3rd level, 17 at 6th, 15 at 9th.
AD&D has 20, 20, 20, 18 and so on.

The result is written to creature +0x1F:

- when a character is made (`6ECD:1860`, 66CE0h);
- at a level-up (`8CCC:181E`, 86D7Eh, and `8E77:0841`, 87A91h).

### Hit points at a level-up

The roll is added at `8E77:0000` (87250h). Each new level's roll is added to
sheet +0x0A. Max HP = +0x0A ÷ the number of classes + CON's bonus for each
level. CON (sheet +0x1D) sets the lowest a level's roll can count for.

### PSP at a level-up

PSP is worked out at `8E77:0162` (873B2h): CON, INT and WIS bonuses, +10 a
level after the first for a psionicist, plus 1 a level for each WIS point
above 15.

### XP table

The table is 8 rows of 20 words, in hundreds of XP: cleric, druid, fighter,
gladiator, preserver, psionicist, ranger, thief. The gladiator's row is the
fighter's. It is read at a level-up (`8E77:096B`, 87BBBh) and by View
Character (`70E7:04B1`, 67D41h).

It differs from AD&D in three places (XP for 2nd level):

| Class | The game | AD&D |
|---|---|---|
| Gladiator | 2,000 (the fighter's) | 2,250 |
| Ranger | 2,200 | 2,250 |
| Thief | 1,200 | 1,250 |

### Spell slots

The routine is `6798:071C` (5E0ACh), which calls `6798:0B32` for each class.
The game gives clerics and druids **3 2 1** at 5th level, where AD&D gives
3 3 1. Rangers' slots are AD&D's.

### Character creation

This is from the code, and checked against the creation screen.

**Ability scores:**

- Each score is rolled **four times as 4d4 + 4 + the race's adjustment, and
  the best counts**.
- It is then raised to the classes' minimum: 17 in the prime requisite (STR
  for fighters and gladiators, WIS for clerics, druids, psionicists and
  rangers, INT for preservers, DEX for thieves).
- Otherwise the minimum is 9 (clerics, fighters, preservers, thieves), 12
  (druids, psionicists), 13 (gladiators) or 14 (rangers).
- A multiclass character takes the highest of its classes' minimums.

**Race adjustments** (STR DEX CON INT WIS CHA):

| Race | STR | DEX | CON | INT | WIS | CHA |
|---|---|---|---|---|---|---|
| Dwarf | +1 | −1 | +2 | 0 | 0 | −2 |
| Elf | 0 | +2 | −2 | +1 | −1 | 0 |
| Half-elf | 0 | +1 | −1 | 0 | 0 | 0 |
| Half-giant | +4 | −5 | +2 | −5 | −3 | −3 |
| Halfling | −2 | +2 | −1 | 0 | +2 | −1 |
| Mul | +2 | 0 | +1 | −1 | 0 | −2 |
| Thri-kreen | 0 | +2 | 0 | −1 | +1 | −2 |

Humans have none.

**Hit points:**

- One die per level of every class: d8 clerics and druids, d10 fighters,
  gladiators and rangers, d4 preservers, d6 psionicists and thieves.
- **Half-giants' dice are doubled.**
- The total is divided by the number of classes (rounded down).
- Then CON's bonus is added for every level: a warrior's full bonus (+1 at 15
  … +7 at 24–25; −1 at 4–6, −2 at 2–3, −3 at 0–1), and at most +2 for levels
  in other classes.
- **There is no automatic maximum at first level.**

**Rerolls:** changing sex or race can make the game roll hit points twice, and
the last roll counts.

**Attacks:** every fighter, gladiator and ranger has 3 halves (3/2 a round)
from level 1, with any weapon. yuan-ti has this (`8E77:038F`).

---

## 4. Thief skills (not in yuan-ti yet)

The game never shows thief skills, but it rolls them. There are eight, in
AD&D's order: pick pockets, open locks, find/remove traps, move silently, hide
in shadows, hear noise, climb walls, read languages. A roll is a d100 that
must come in under the chance. The chance is:

1. **A base per skill:** 28, 18, 13, 28, 18, 23, 78, −4.
2. **+4 per thief level.**
3. **AD&D's racial adjustments.** For example, a dwarf gets +10 open locks,
   +15 find traps, −10 climb walls and −5 read languages.
4. **DEX, for the first five skills:**
    - −5 for each point below 12, 11, 12, 13, 11;
    - +5 for each point above 16, 15, 17, 16, 16;
    - −3 for each point above 21, 20, 21, 19, 19.
5. **An equipment penalty** of 5, 0, 0, 10, 5, 0, 10, 0 when the thief has
   **anything at all** in the leg armour slot, the quiver or either hand.
    - The slots are a list in the game's data: legs, quiver, left hand, right
     hand.
    - The check ignores what the item is, and ignores chest, arm and head
     armour, so a thief holding any weapon pays it. The manual's "anything
     but leather armour" is not what the code does.
6. **The situation's bonus or penalty**, given by the script that asks.

**Who can use them:**

- Only characters with thief levels have the skills. Everyone else's chance
  is 0.
- The character's condition must be **Okay**. In the original, a character
  still on the creation screen (New) is not Okay, so its skills show as 0
  there.

**Effects work through the situation's number.** Ruling a skill out takes
1000 off it, and making it certain adds 1000:

- Blind, Afraid, Confused, Berserk and Paralyzed stop all of them, except that
  a blind thief can still hear noise.
- Slowed stops all but pick pockets.
- Fire Shield stops pick pockets and hiding. Mirror Image stops hiding.
- Graft Weapon stops pick pockets, open locks and climb walls.
- Feeblemind stops read languages.
- Enlarge scales the situation's number, not the skill: for hiding it is
  divided by (100 + 10 × level)%, and for climbing it is multiplied by that.
- Detect Traps makes finding traps certain, for anyone, thief or not.
- Invisible makes hiding certain.
- **A bug:** one rule would make move silently certain, and hear noise
  impossible, for someone wearing one particular leg item. The code passes
  its two lists the wrong way round, so it never applies.

**Who rolls them.** No game code rolls a skill on its own; only scripts do.
Across all of GPLDATA.GFF there are:

- **13 skill checks:**
    - find/remove traps ×4 (−10 to +3);
    - open locks ×4 (−4 to +8);
    - climb walls ×3;
    - pick pockets ×1;
    - hear noise ×1 (a party check).
- **50 trap triggers in 16 scripts:** the party's best at find/remove traps
  rolls first, with no bonus. Success, or Detect Traps on the character who
  set the trap off, avoids it.
- **3 ability checks:** a d20 under the ability, CHA twice and STR once.
- A party check always goes to the member with the best chance. A script
  command for each member in turn exists, but no script uses it.

**Move silently, hide in shadows and read languages are never rolled** in the
original.

---

## 5. Original bugs that Obsidian Edition fixes

Obsidian Edition fixes these in its patched copy of the game; each was found in the code and seen in fights. yuan-ti's `CombatFixes`
already covers Mirror Images and blows, Brave against fear, DEX against areas,
Negative Plane Protection, Absorb Disease, Berserk sides, walls and the fire
elementals. These are more candidates, each off by default to stay faithful:

| Bug | In DSUN.EXE | What the game does |
|---|---|---|
| **Mirror Image eats your own side's spells** | `80F5:1999` (7A539h), the d100 `cmp ax,4Bh` at `80F5:1A2D` (7A5CDh) | The images' 75% check has no exception for the caster's side. Armor cast on oneself under Mirror Image is lost 3 times in 4, and an image goes with it. Blows skip the roll (they come with no spell), so images stop none; yuan-ti knows that part. |
| **Armor uses two charges a hit** | the `je` at `80F5:19B7` (7A557h); the damage routine `80F5:0E9E` (79A3Eh) | The landing routine spends a charge for any spell or blow, a friend's Bless included. The damage routine spends another for the damage. Obsidian Edition's fix: one charge per damaging hit. |
| **Share Strength's end** (any maintained power on someone else) | the loop at `6698:063C`–`0684` (5CEECh–5CF34h) | It removes each effect record of the power with the psionicist as the owner instead of the record's own owner, so the receiver keeps the effect and one of the psionicist's own records with that handle goes instead. After a removal the records move down, so the next one is skipped. It also counts to 192, not to the live count at `DS:1E24`. |
| **Preserver's Dispel Magic** (record 27) | the record's effect byte (+0x19) is 50, No Spells | It also puts No Spells on everyone it catches, the caster's party included, and Dispel Magic can't remove that. The cleric's (99) has no effect. |
| **Hold Undead holds the living** | see 1.3 | |
| **Cause Serious / Critical Wounds** (114, 129) | their records' dice | 2d9 and 3d9, where the Cure spells heal 2d8+1 and 3d8+3. |
| **Thri-kreen claws are crushing** | `612E:151F` (581DFh), in the attack routine `612E:1359`, sets every natural attack's damage kind to crushing (8) | A creature immune to crushing (the mastyrials) is safe from a thri-kreen's claws. Obsidian Edition makes a party thri-kreen's claws edged (0x10). |
| **Scare: no one gets the elf / half-elf exception** | the Scare save code | It asks for a creature that is both an elf and a half-elf. As a result, 6th level and up always save and everyone below never can. |
| **A thief skill rule with swapped lists** | see section 4 | |
| **A click on the Effects screen ends a spell** | two jumps in the click handler, `8640:0C06` (7F226h) and `8640:0C16` (7F236h) | A stray click loses a spell. Powers are meant to end this way, since that is how a power is dropped; spells probably aren't. |
| **The roster's DELETE with the list scrolled** | Create Characters' roster | It deletes a different character from the one chosen. |
| **A New character's thief skills show 0** | the Okay test (section 4) | |
| **The Trustee in the slave pens can stop for good** | his GPL rounds | His two pauses take their corner from numbers other pen scripts change, so he can set off for a spot that doesn't exist and stay busy ("I'd love to talk more...") for good. |
| **The elf Cilla (the game's own party) has the half-elf woman's figure** | data | |
| **Sling +1 has a blank icon** | data | Its picture is missing from the game's data. |

**The acid check** (`80F5:2024` armour, `80F5:1EBA` weapon):

- A weapon goes on a d20 under 8 − its plus.
- Armour is tested on its +0x0F byte (the spell it carries), not its plus. A
  piece with no spell is destroyed outright: Tanelyv's Armor +2 goes as surely
  as plain leather.
- yuan-ti's `CorrodeArmor` reads the same byte.

Obsidian Edition also has a rule switch that gives each item AD&D's save by
material.

---

## 6. Saving throws: compare with yuan-ti's reference

Most of this matches yuan-ti's `dsun-exe-reference.md` §8. Points to compare:

**Which save:**

- A spell's save kind 5 maps to the sheet's **third** save
  (petrification/polymorph), through the table at `DS:1E75`. Almost every
  spell uses it.
- Kind 1 is paralysis/poison/death: the clouds, Poison, Slay Living and the
  psionic attacks.
- The spell save is never used.

**The doubled d20.** The d20 counts double whenever the spell's damage kind is
fire, cold or electricity:

- fire: Burning Hands, Flaming Sphere, Fireball, Flame Arrow, Minute Meteors,
  Fire Shield, Wall of Fire (both), Focus Heat, Produce Fire, Flame Strike;
- cold: Chill Touch, Cone of Cold;
- electricity: Shocking Grasp, Lightning Bolt.

Nothing else is doubled. That excludes acid, crushing (Ice Storm), poison
(Cloudkill), draining and psionics.

**Modifiers:**

- The save routine's sum of modifiers starts at 0 (`xor si,si` at `80F5:11A7`,
  79D47h).
- WIS applies against mind spells, charms, holds, fear and illusions: −6 at 1,
  −4 at 2, −3 at 3, −2 at 4, −1 at 5–7, +1 at 15, +2 at 16, +3 at 17, +4 at
  18+.
- CON applies on paralysis/poison/death saves: −2 at 1, −1 at 2, +1 at 19–20,
  +2 at 21–22, +3 at 23–24, +4 at 25.
- **Dwarves and halflings also add CON × 2 ÷ 7.**
- Druids get +2 against fire and electricity. Psionicists get +2 against mind
  spells and charms.
- A target the caster can't see gets +4 against a single-target spell (the
  caster is Blind, or the target is Invisible and undetected).

**Special cases:**

- Against Cloudkill, creatures of 6th level or lower get no save.
- Against Chaos, only warriors can save.
- Against Dismissal, the target adds its level and subtracts the caster's.
- Against Scare, see section 5.

**No items give saves:** the game has no rings or cloaks of protection.
Obsidian Edition adds them as new items.

---

## 7. Other things confirmed in play, useful as test cases

**Initiative:**

- 20 + 0–9 + the DEX reaction + Hasted +2, Slowed −2, Blind −2.
- A 0–199 tie-breaker decides equal scores.
- **Wait sets the score to 10**, or lowers it by one if it is already 10 or
  less.
- There are no weapon speeds.

**No critical hits.** A natural 20 hits and a natural 1 misses, and the d20
never reaches the damage routine. A backstab is the only damage multiplier.

**A spell's caster level** is the caster's highest level in a class that
shares a sphere with the spell. An elemental spell cast by a priest of another
element counts as level 0.

**Clerics never get the common 4th and 5th-level priest spells:** the table
marks them druid-only.

**Psionics:**

- A power works at the psionicist level. Anyone else with psionics counts as
  level 1.
- Kept-up powers re-cost their upkeep each round and drop when PSP runs out.
- A psionic attack makes the defender raise the best defence mode it knows and
  can pay for.
- Mind Bar adds 75% magic resistance against mind spells.
- The costs are in the table at the load segment + 3FB9h, 8 bytes a power:
  +1 the cost to use, +2 the cost to keep up (63h = none), +5 FEh for a
  defence mode. Enhanced Strength and Domination cost 0 to start.

**Searching junk, hay and wardrobes** (GPL):

- Junk is 0–14, hay and wardrobes 0–10.
- 0–1 finds nothing in junk, and 0–2 in hay and wardrobes.
- After 6 finds (junk and hay counted separately) or 6 wardrobe searches,
  nothing more is found.
- The scripts have outcomes for the low rolls, but the "nothing" check comes
  first, so they never happen. **This is a good test that the VM keeps that
  order.**

**The arena's count of fights** is GPL global number 12. It goes up by 1 each
time the party walks out of the arena gate after being called, however many
waves came. Kurzak calls once 8 h 05 min of game time have passed since the
last trip, and the clock stands still while the Look pointer is chosen.

| Count | What happens |
|---|---|
| 0–3 | A speech of its own for each trip |
| 4 | Scar's challenge, if the script's check on Scar passes |
| 1000 | A special speech, and **El's Drinker +2** and a **Bone Gythka +3** appear at the party's feet (checked in play, with the count set in memory) |
| 5000 | No fight: the announcer sends the party back, the crowd throws money, and flag 10 and global 49 = 3 mark the war won; the final battlefield then plays the victory (checked in play) |

---

## 8. The dice log as a test oracle

yuan-ti already ports Borland's random number generator (`BorlandRandom`).
Obsidian Edition's dice log records **every call the original makes to it**,
and labels most of them by the code that called (each call site is known by
the bytes of its caller). The log shows each roll's dice and what it was for:

- attacks, with every to-hit term;
- damage;
- saving throws;
- initiative and the tie-breaker;
- thief skills;
- character creation;
- level-up hit points;
- Mirror Image's d100;
- Confusion's d10;
- and more.

Two ways to use it:

- **Golden fights:** play a fight in the original with the dice log on, then
  replay it in yuan-ti from the same seed and compare the order and purpose
  of the rolls. A difference in order shows a rule run in the wrong order,
  even when the totals look right.
- **Rule checks:** each log line shows what a roll needed and where each bonus
  came from, which makes a quick reference for expected numbers.

To get it, run Obsidian Edition's Loader with the GOG game. `python -m
dsloader dicelog` prints the same log in a terminal.

---

## 9. Ease-of-use features Obsidian Edition built and tested in play

For yuan-ti's roadmap §11:

- **A combat log on the map** that gives each roll's terms: what the d20
  needed, and where each bonus came from (THAC0, AC, from behind, the
  weapon's plus...). It is drawn in the game's own font, and it can sit in a
  strip below the map instead, so that it covers nothing.
- **Tab and Enter targeting** in fights: Tab picks the nearest enemy, Enter
  attacks it, with no mouse aiming.
- **Map scrolling** with the mouse wheel and with right- or middle-drag.
- **More save slots** (40 on four pages) and **saves named after the party**.
- **THAC0, saves, thief skills and spell slots** on the game's own screens,
  and a Look box that says what can hurt a monster ("only +1 or better
  weapons").
- **Damage numbers over creatures.** Keep them clear of any panel that can
  cover the map's corner.
- **A game speed setting.**
- **A switch for each rule change**, so a player can keep the original rules.
  It is the same idea as yuan-ti's `CombatRules` and `CombatFixes`.

---

## 10. More detail

Obsidian Edition's public documentation (branch `main`):

- `obsidian-edition/HOW-THE-GAME-WORKS.md`: the original game's rules, from
  its code and checked in play (sections 3, 4, 6 and 7 here come from it).
- `obsidian-edition/DEVELOPMENT.md`: the memory map, the save format, and how
  each fix and rule is patched, with DSUN.EXE offsets.

Repository: github.com/daaki85/darksun-obsidian-edition
