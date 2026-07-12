# 🎬 Kinetic Novel

A kinetic novel is a story without a single choice: the player only advances the lines, and all the drama is carried by the staging — here the engine works not as a branching machine but as a director.

## What the example does

A night railway station in the rain: music plays from the very first frame, rain pours, and the scene gently fades in from transparency. The actress Amber slides in from the left, stops, and "breathes" — pulsing almost imperceptibly. On a memory, the camera jolts and a flash fires. Then the frame changes: darken, a new dawn background, rain and music turn off, the scene fades back in. In the finale a short scale "pop" puts a period on the scene, and everything fades to black.

## The engine as a director

In a kinetic novel you have no branches — but you do have the full set of staging tools, and every one of them is used in the example:

- **Background** — `bg /content/bg/night_station.jpg` sets the frame; changing `bg` changes the "location".
- **Frame transitions** — `fade` (full-screen fade to `black`/`white`/`clear`), `dim` (dim the scene for focus, `alpha=0` to restore), `flash` (a short flash).
- **Camera** — `camera action=shake …` adds a physical accent.
- **Particles** — `particles type=rain on=true` lays down a rain layer, `on=false` removes it.
- **Sound** — `audio channel=music action=play …` starts music, `action=stop` silences the channel.
- **Actor animation** — `anim` and `move` bring Amber to life: an entrance across the screen, "breathing", swaying, the final accent.

## Animation: one rule and two notations

There is exactly one rule: **different channels run in parallel, while keys within one channel play in sequence.** "Breathing" (the `scale` channel) and swaying (the `rotation` channel) are two separate `anim` lines, and they play simultaneously on top of each other. But multiple keys within `scale` play back one after another.

You can write an animation three ways:

1. **One-liner `to=`** — a tween from the current value to the target in one line:
   `anim amber scale to=1.08 dur=0.4 ease=outBack`
2. **Bracket list `[…]`** — a set of values stretched across the duration:
   `anim amber scale [1 1.03 1] 3s yoyo`
3. **Keyframes `keys=`/`path=`** — "time:value" pairs:
   `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`

**CRITICALLY important quoting rule** (the most common mistake): values with **spaces** inside quotes — `keys="…"` and `path="…"` — require the **legacy form** with `id=`/`prop=`. The terse form breaks on spaces. The bracket list `[…]` and the one-liner `to=`, however, work fine in the terse form (`anim amber scale …`).

Compare two real lines from the example. The head sway uses keys with spaces, hence the legacy form:

```
anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine
```

And the actress's entrance is a path with spaces, also legacy `id=`/`path=`:

```
move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic
```

"Breathing", on the other hand, is a bracket list with no spaces inside quotes, so the terse form works: `anim amber scale [1 1.03 1] 3s yoyo`.

## Engine features used here

All quoted from `kinetic-novel.lvns`:

- **Sound:** `audio channel=music action=play url="/content/audio/rain_theme.ogg"` and `audio channel=music action=stop`
- **Particles:** `particles type=rain on=true` / `particles type=rain on=false`
- **Fade-in:** `fade to="clear" duration=1.2` and the final `fade to="black" duration=1.5`
- **Movement across the screen:** `move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic`
- **"Breathing" (bracket + yoyo):** `anim amber scale [1 1.03 1] 3s yoyo`
- **Swaying (legacy keys + rotation):** `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`
- **Camera shake:** `camera action=shake amplitude=0.02 duration=0.4`
- **Flash:** `flash to="white" duration=0.3`
- **Dim/restore:** `dim alpha=0.6 duration=0.5` … `dim alpha=0 duration=0.8`
- **Final "pop" (one-liner to= + outBack):** `anim amber scale to=1.08 dur=0.4 ease=outBack`

## Step-by-step breakdown

1. **Atmosphere.** Set the background, music, and rain right away, then gently fade the frame in:
   ```
   bg /content/bg/night_station.jpg
   audio channel=music action=play url="/content/audio/rain_theme.ogg"
   particles type=rain on=true
   fade to="clear" duration=1.2
   ```
2. **The actress's entrance + "breathing".** Declare the actor, slide her across the screen (`move`), and start the looping breathing (`anim scale … yoyo`) — these are two parallel channels:
   ```
   actor amber left neutral
   move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic
   anim amber scale [1 1.03 1] 3s yoyo
   ```
3. **Swaying.** Add a third channel — `rotation` via keys. It plays on top of the "breathing" without cancelling it: `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`
4. **Memory accent.** On the line from the past, shake the camera and fire a flash:
   ```
   camera action=shake amplitude=0.02 duration=0.4
   flash to="white" duration=0.3
   ```
5. **Frame change.** The classic "darken → change → reveal" combo: dim the scene, change the background, turn off the rain and music, bring the light back:
   ```
   dim alpha=0.6 duration=0.5
   bg /content/bg/empty_platform_dawn.jpg
   particles type=rain on=false
   audio channel=music action=stop
   dim alpha=0 duration=0.8
   ```
6. **Final "pop" and ending.** A short scale bump as the scene's closing beat, then a fade to black:
   ```
   actor amber center smile
   anim amber scale to=1.08 dur=0.4 ease=outBack
   fade to="black" duration=1.5
   -> __end
   ```

## Build and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/kinetic-novel/kinetic-novel.lvns -o /tmp/kn.lvn

# structural check
/tmp/lvnconv validate /tmp/kn.lvn
```

The goal is **0 warning(s)**. An important point: an invalid `anim`/`move` form (for example, `keys=`/`path=` with spaces without legacy `id=`/`prop=`) produces a compile error, not a silent skip. That is the safety net — the transcoder will not let broken staging into the container.

## Make it yours

- Play with easing and keys: swap `ease=outBack` for `outCubic`, add more keys in `keys="…"` for a more complex sway.
- Run parallel channels: two `anim` lines (for example, `scale` and `alpha`) on one actor play simultaneously.
- Build a chain of frames: several `dim → bg → dim alpha=0` combos to walk the hero through locations.
- Pick other `particles` (`snow`) and `camera` moves (`zoom`/`pan`) to match the scene's mood.
- Add sound accents via `audio channel=sfx action=play …` on key lines.
- Full voice-over: a `voice "/content/voice/xxx.ogg"` line before each spoken line — the clip plays with the text, and the next line silences it (see the "Voicing lines" recipe in recipes.md).

## Next

- [Language reference](../LANGUAGE.md)
- [Animation system](../../docs/animation-system.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
