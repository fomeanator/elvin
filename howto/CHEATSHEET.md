# `.lvns` Cheatsheet (one page)

A dense syntax map. Details — [`LANGUAGE.md`](LANGUAGE.md);
capabilities/limits — [`CAPABILITIES.md`](CAPABILITIES.md).

```
scene my-game                  // (recommended) chapter header
// comment
```

## Text
```
Just a plain line.             // narration
Mara: A spoken line.           // speaker: text
Mara [smile]: A spoken line.   // + cast emotion (emotion axis)
voice "/content/voice/a1.ogg"  // voiceover for the NEXT line (auto-stops on a new one)
voice "/content/voice/a1.ogg"  // voiceover for the NEXT line (auto-stops on a new one)
actor_map Mara=mara            // bind Name ↔ cast id
«A long text,                  // guillemets = multiline
 over a couple of lines.»
Gold: {gold}, atk {atk+5}.     // interpolation {expression}
{{ and }}                      // literal curly braces
```

## Choices and jumps
```
- Option A -> labelA           // menu (consecutive «- » lines)
- Option B -> labelB cost="3 turns"
- Hidden -> labelC expr="gold >= 5"       // option hidden when false
choice timeout=10 timeout_goto=late       // ⏱ timer for the NEXT menu: a bar
- Make it -> ok                           //   above the buttons, expired → late
input var=name prompt="Who are you?" default="Guest" max=24   // text input → {name}
-> label                        // unconditional jump (goto)
:label                          // target label
-> __end                        // built-in ending
```

## Conditions and state
```
gold = 12                       // assign (declaration = mutation)
gold = gold - 6
name = "Mara"   inv = []        // string, empty list
if gold >= 10 -> rich           // true → jump; otherwise fall through
if has(inv,"key") {             // if/else block
  ...
} else {
  ...
}
```

## Loops, subroutines, functions
```
for it in inv { Item: {it}. }
while xp >= need { xp = xp - need  level = level + 1 }
call fight                      // jump with return
return                          // come back after call
func add(a,b){ return a + b }   // function (sugar over call/return)
s = add(2,3)                    // call with a return value
save                            // snapshot (default slot)
load
```

## Staging
```
bg /content/bg/room.jpg                 // background
actor mara left smile                   // character: id, position, emotion/pose
actor hero center w=.5 h=.6 x=.5 armor={arm}
actor mara hide                         // hide
obj id=key sprite_url="/ui/key.png" x=.2 y=.7 anchor="0.5,0.5" on_click="take"
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}  💰{gold}»   // reactive HUD (200ms)
text hud hide
```
Positions: `far_left left center_left center center_right right far_right`.
Fields: `w`(width) `h`(height) `x` `y` `scale` `anchor="ax,ay"` `z` `flip` `rotation` `opacity` `on_click`.

## Effects / sound / timing
```
fade to="black" duration=0.8     dim alpha=0.6 duration=0.5     flash color="white" duration=0.3
tint ...    blur ...
camera action=shake amplitude=0.02 duration=0.4      // shake/zoom/pan/reset
particles type=rain on=true                          // rain/snow
audio channel=music action=play url="/a.ogg"         // music/sfx/ambient; play/stop
wait ms=500
```

## Animation (channels || in parallel, keys within a channel run in order)
```
anim mara scale to=1.1 dur=0.4 ease=outBack          // one-liner (terse ok)
anim mara scale [1 1.03 1] 3s yoyo                   // bracket list (terse ok)
anim id=mara prop=rotation keys="0:0 1:8 2:-8 3:0" loop=yoyo ease=inOutSine  // keys= → legacy id=/prop= ONLY
move id=mara path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic                  // path= → legacy id= ONLY
anim mara stop
```
props: `x y screen_x screen_y scale scalex scaley rotation alpha frame` · ease: `linear inOutSine outCubic outBack inBack` · loop: `once|restart|yoyo`.

## Built-in functions (expressions)
```
rand() rand(n) rand(a,b)  chance(p)  min(a,b) max(a,b)  abs floor round   // NO ceil
len(x) has(coll,x) get(coll,k[,def]) indexof(arr,x) count(arr,x) sum(arr) first(arr) last(arr) keys(o) vals(o)
list(...) push(arr,x) pop(arr) removeat(arr,i) remove(arr,x) slice(arr,s[,e]) concat(...) put(m,k,v) del(m,k)
```
Operators: `+ - * /` · `== != > >= < <=` · `&& || !`. An unset variable = `0`/`""`/`false`.

## Build / validate
```
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i game.lvns -o /tmp/game.lvn
/tmp/lvnconv validate /tmp/game.lvn        # goal: OK ... 0 warning(s)
```

## ⚠ Limit traps
- The cast is defined in `manifest.json`/the `cast` block, **not in `.lvns`** (there you only get `actor <id>`).
- `keys=`/`path=` with spaces → use the `id=`/`prop=` form (compile error otherwise).
- `hint text="…" duration=6` — a popup at the top center; `show=false` removes it, `duration>0` auto-hides. `cost`/`requires_stat` do **not** deduct resources themselves — deduct explicitly via `set`/`inc`.
- No `ceil` (round with arithmetic). A timer and text input DO exist: `choice timeout=` and `input var=`.
- Before a jump-target label that can be «fallen into» from above, put `-> __end`/`-> label`.
