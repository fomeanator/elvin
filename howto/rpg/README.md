# ⚔ RPG

A compact role-playing game: stats, a hub town, a shop, random battles and leveling up — all in a single `.lvns` file without one line of native code.

## What the example does

The hero arrives in Oakvale with base stats (level, ♥hp, attack, gold, potions) and
roams the square freely: to the merchant for potions and a sword, into the forest for
a random fight with a wolf, or to the pack leader — the boss. Battles are turn-based:
attack, potion or an escape attempt, answered by an enemy strike. Victories bring gold
and XP; once the threshold is reached the hero gains a level (maxhp/attack grow).
There are two endings: triumph over the boss or death in the forest.

## Engine features used here

- **Reactive `text hud`** — the stat bar updates itself as the fight goes on:
  `text hud x=3 y=8 size=42 color=#f1e4c9 «Lv.{level}  ♥{hp}/{maxhp}  atk {atk}  💰{gold}  🧪{potions}»`
- **Stat variables** as game state: `level = 1`, `xp = 0`, `hp = 20`, `atk = 4`, `gold = 10`, `potions = 1`.
- **A shop on inline `if/else`** with a wallet check:
  `if gold >= 5 { gold = gold - 5  potions = potions + 1  Merchant: Save it for a rainy day. } else { Merchant: Not enough coin. }`
- **Combat as a subroutine** — a single `call fight` / `return` entry point instead of copy-paste.
- **`while` for leveling up** — it may trigger several times in one fight:
  `while xp >= need {`
- **Math and randomness**: `dmg = max(1, atk + rand(0,3))`, `hp = min(maxhp, hp + 12)`,
  `if chance(0.5) -> fled`, `need = floor(need * 1.5)`.

## Step-by-step walkthrough

**1. The stat block.** The game starts by initializing variables — this is the
character sheet. Declaring them explicitly keeps the balance readable:

```
level = 1
xp = 0
need = 8
maxhp = 20
hp = 20
atk = 4
gold = 10
potions = 1
```

**2. Reactive HUD.** One `text hud` line with `{...}` interpolation — and the panel
redraws itself every time any of the variables changes. No need to poke it
separately.

**3. The town hub.** The `:town` label is a choice crossroads. Each option leads
to its own label and returns back to town:

```
:town
The square. Where to?
- 🛒 Visit the merchant (potions, sword) -> shop
- 🌲 Into the forest (fight) -> forest
- 🏆 Face the pack leader -> boss
```

**4. A shop with a money check.** Every purchase is an `if` on available gold: if
there is enough — deduct it and hand over the goods, otherwise refuse. The sword
raises attack permanently (`atk = atk + 3`), potions accumulate in `potions`.

**5. The combat engine as a reusable subroutine — the key part.** Before a fight
the script simply fills in the "enemy parameters" with plain variables and calls the shared code:

```
:forest
bg /content/bg/forest.jpg
A wolf leaps out of the bushes!
ename = "Wolf"
ehp = 12
eatk = 5
goldw = 6
xpw = 5
call fight
bg /content/bg/town.jpg
-> town
```

Inside `:fight` is the turn loop. The player gets a choice of attack / potion / flee:

- **Attack** (`:f_atk`): `dmg = max(1, atk + rand(0,3))`, subtract from `ehp`, and if
  `if ehp <= 0 -> f_win`, otherwise pass to the enemy's response `-> f_enemy`.
- **Potion** (`:f_pot`): first `if potions >= 1 -> do_pot`; in `:do_pot` spend a potion
  and heal with a cap `hp = min(maxhp, hp + 12)`.
- **Flee** (`:f_flee`): `if chance(0.5) -> fled` — luck lets you go with `return`, otherwise
  the enemy strikes back.

**Enemy response** (`:f_enemy`): `edmg = max(1, eatk - rand(0,2))`, subtract from `hp`, and
`if hp <= 0 -> dead`, otherwise the battle loop continues `-> fight`.

**Victory** (`:f_win`): grant the reward from those same parameters, level up and exit:

```
:f_win
{ename} is defeated! +{goldw}💰 +{xpw} XP.
gold = gold + goldw
xp = xp + xpw
call levelup
return
```

**6. Leveling up via `while`.** The accumulated XP may be enough for several
levels at once — hence a loop, not a single `if`:

```
:levelup
while xp >= need {
  xp = xp - need
  level = level + 1
  maxhp = maxhp + 5
  hp = maxhp
  atk = atk + 1
  need = floor(need * 1.5)
  ✨ New level — {level}!
}
return
```

**7. Endings.** `:victory` — the forest is free after the boss; `:dead` — death with
`fade to="black"`. Both lead to `-> __end`.

## Why combat is a subroutine

The same `:fight` block serves both the wolf and the boss. The only difference
between them is the values of the `ename/ehp/eatk/goldw/xpw` variables, which the
script sets right before `call fight`. This way the whole combat mechanic (turns,
damage, potions, fleeing, the enemy's response, the reward, the level-up) lives in
one place: to add a new monster you don't copy the logic — just set its parameters
and call the same subroutine. `return` sends the flow exactly back to where the
`call` was, so after a forest fight we land back in town, and after the boss — in
the ending.

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/rpg/rpg.lvns -o /tmp/rpg.lvn

# structural check: the goal is 0 warning(s)
/tmp/lvnconv validate /tmp/rpg.lvn
```

## Make it your own

- **New enemies.** Create a label (say `:cave`), set your own
  `ename/ehp/eatk/goldw/xpw` and call `call fight` — a ready-made monster.
- **New weapons and armor in the shop.** Add an option to `:shop` and an `if` purchase
  modeled on the sword; armor can introduce a `def` variable that you subtract from `edmg`.
- **Classes and spells.** Keep the class and mana in variables, add a
  `- 🔥 Spell` branch to `:fight` with an `if mana >= ...` check and damage to `ehp`.
- **An item inventory.** Use a list (`inv = []`, `push`/`has`/`remove`) and a
  `for it in inv` loop for the bag screen.
- **Multiple locations.** Extend `:town` with links to new hubs (a village, a cave,
  a castle), each with its own fights and merchants.

## Next

- [Language reference](../LANGUAGE.md) — the full `.lvns` syntax.
- [Recipe book](../recipes.md) — short reusable patterns.
- [All genres](../README.md) — the genre map and quick start.

When you want a bigger scale — look at the full-size RPG
`server/content/scripts/rpg-inv.lvns` (stats + a proper inventory) and the
standalone battle scenario `server/content/scripts/goblin-battle.lvns`.
