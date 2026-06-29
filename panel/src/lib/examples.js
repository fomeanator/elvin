// A library of ready LVNScript examples, one per feature. Click one in the IDE
// to load it into the editor (Ctrl+Z brings your own code back). Every example is
// a complete, clean-compiling mini-chapter.

export const EXAMPLES = [
  // ── Lesson ────────────────────────────────────────────────────────────────
  // The guided first lesson: load it, read the comments top-to-bottom, press
  // ⌗ Compiled / Save to app to run it. By the end you've built a complete
  // branching novel — scene, background, character, emotion, choice, endings.
  { cat: "Lesson", title: "Lesson 1 — your first novel", code:
`// ===== LESSON 1: your first novel =====
// Read each step, then press "Save to app" to play it. Edit freely — Ctrl+Z undoes.

// STEP 1 — every chapter opens with "scene <name>". Plain lines are narration.
scene first-night
Rain drummed on the porch roof.

// STEP 2 — set the stage: a background, then bring a character on screen.
bg id="porch"
actor id="mara" show=true position="center"

// STEP 3 — dialogue is "Name: text". Tip: start typing a name and the editor
// suggests your cast. Add a mood in [brackets] — type "Mara [" to see her moods.
Mara [happy]: You came back. I wasn't sure you would.

// STEP 4 — a choice. Each "- text -> label" jumps to a matching ":label" below.
Mara: Will you stay the night?
- Stay -> stay
- Head home -> leave

// STEP 5 — labels mark where a choice lands. End every path with "goto __end".
:stay
Mara [happy]: Then come in out of the rain.
goto __end

:leave
Mara [sad]: ...travel safe, then.
goto __end

// STEP 6 — your turn: change the lines, add a 3rd choice, or a new character.
// You now know everything needed for a simple novel. ` },

  // ── Basics ──────────────────────────────────────────────────────────────
  { cat: "Basics", title: "Narration", code:
`scene narration
The rain never stopped that night.
She waited by the window, listening.
goto __end` },
  { cat: "Basics", title: "Speech", code:
`scene speech
Mara: You came back.
Mara: I wasn't sure you would.
goto __end` },
  { cat: "Basics", title: "Speech + emotion", code:
`scene emotion
actor_map Mara=mara
Mara [smile]: Then come in out of the rain.
Mara [sad]: ...if you still want to.
goto __end` },
  { cat: "Basics", title: "Two speakers", code:
`scene dialogue
Mara: Who's there?
Stranger: Only the wind.
Mara: The wind doesn't knock.
goto __end` },
  { cat: "Basics", title: "Comments", code:
`scene comments
// lines starting with // are notes — stripped on compile
Mara: This line ships.
// TODO: add a choice here later
goto __end` },

  // ── Choices ─────────────────────────────────────────────────────────────
  { cat: "Choices", title: "Basic choice", code:
`scene choice
Mara: Stay, or go?
- Stay -> stay
- Go -> leave
:stay
Mara [smile]: Good.
goto __end
:leave
Mara: ...
goto __end` },
  { cat: "Choices", title: "Cost-gated", code:
`scene gated_cost
Merchant: The lantern is yours — for a price.
- Buy it -> bought cost="50 gold"
- Walk away -> leave
:bought
You take the lantern.
goto __end
:leave
goto __end` },
  { cat: "Choices", title: "Stat-gated", code:
`scene gated_stat
Guard: Only the brave pass.
- Force the door -> enter min=5 requires_stat="courage"
- Turn back -> leave
:enter
The door gives way.
goto __end
:leave
goto __end` },
  { cat: "Choices", title: "Three branches", code:
`scene three_way
Mara: Which road?
- North -> north
- East -> east
- Home -> home
:north
You head north.
goto __end
:east
You head east.
goto __end
:home
You go home.
goto __end` },
  { cat: "Choices", title: "Loop until chosen", code:
`scene hub
:menu
Guide: Pick a topic.
- Weather -> weather
- Leave -> __end
:weather
Guide: Rain, mostly.
goto menu` },

  // ── Variables & flow ────────────────────────────────────────────────────
  { cat: "Logic", title: "Set + interpolate", code:
`scene vars
set key="name" value="Mara"
set key="score" value=3
Mara: Hello, {name} — score is {score}.
goto __end` },
  { cat: "Logic", title: "Increment", code:
`scene inc
set key="gold" value=0
inc key="gold" by=10
inc key="gold" by=5
Merchant: You have {gold} gold.
goto __end` },
  { cat: "Logic", title: "Computed set (expr)", code:
`scene expr_set
set key="base" value=3
set key="bonus" value=2
set key="total" expr="base * 2 + bonus"
Mara: Total is {total}.
goto __end` },
  { cat: "Logic", title: "If / then / else", code:
`scene branch
set key="trust" value=4
if expr="trust >= 3" then="friend" else="wary"
:friend
Mara [smile]: I trust you.
goto __end
:wary
Mara: ...we'll see.
goto __end` },
  { cat: "Logic", title: "Counter loop", code:
`scene counter
set key="n" value=0
:tick
inc key="n" by=1
Mara: Tick {n}.
if expr="n >= 3" then="done" else="tick"
:done
Mara: Done.
goto __end` },
  { cat: "Logic", title: "Call / return", code:
`scene subroutine
Mara: Let me check.
call check
Mara: All clear.
goto __end
:check
You glance around the room.
return` },
  { cat: "Logic", title: "Once-only gate", code:
`scene once
:room
if expr="seen == 0" then="first" else="again"
:first
set key="seen" value=1
Mara: First time here.
goto room
:again
Mara: Back again.
goto __end` },
  { cat: "Logic", title: "Boolean flag", code:
`scene flag
set key="lied" value=true
if expr="lied" then="caught" else="ok"
:caught
Mara: I know you lied.
goto __end
:ok
Mara: Honest, then.
goto __end` },

  // ── Background ──────────────────────────────────────────────────────────
  { cat: "Background", title: "Set a background (url)", code:
`scene bg_url
bg sprite_url="/content/bg/porch.jpg"
The porch light flickered.
goto __end` },
  { cat: "Background", title: "Background by catalog id", code:
`scene bg_id
bg id="porch"
You step onto the porch.
goto __end` },
  { cat: "Background", title: "Change scene mid-chapter", code:
`scene bg_change
bg id="porch"
Mara: Come inside.
fade to="black" duration=0.5
bg sprite_url="/content/bg/porch.jpg"
fade to="clear" duration=0.5
Mara: Warmer in here.
goto __end` },

  // ── Actors ──────────────────────────────────────────────────────────────
  { cat: "Actors", title: "Show a character", code:
`scene actor_show
actor id="mara" show=true position="center"
Mara: Here I am.
goto __end` },
  { cat: "Actors", title: "Positions", code:
`scene actor_pos
actor id="mara" show=true position="left"
Mara: Over here.
actor id="mara" show=true position="right"
Mara: ...and now here.
goto __end` },
  { cat: "Actors", title: "Pose + emotion (catalog)", code:
`scene actor_state
actor id="mara" show=true pose="standing" emotion="happy" position="center"
Mara: Feeling good today.
actor id="mara" emotion="sad"
Mara: ...or not.
goto __end` },
  { cat: "Actors", title: "Fade a character in", code:
`scene actor_enter
actor id="mara" show=true position="center" enter="fade" transition_duration=0.6
Mara: I fade in.
goto __end` },
  { cat: "Actors", title: "Fade a character out", code:
`scene actor_exit
actor id="mara" show=true position="center"
Mara: Goodbye.
actor id="mara" show=false exit="fade" transition_duration=0.6
The room is empty now.
goto __end` },
  { cat: "Actors", title: "Slide in / out", code:
`scene actor_slide
actor id="mara" show=true position="left" enter="slide_left" transition_duration=0.5
Mara: I slide in from the left.
actor id="mara" show=false exit="slide_right" transition_duration=0.5
goto __end` },
  { cat: "Actors", title: "Flip a sprite", code:
`scene actor_flip
actor id="mara" show=true position="center" flip=true
Mara: Now I face the other way.
goto __end` },
  { cat: "Actors", title: "Two characters", code:
`scene two_actors
actor id="mara" show=true position="left"
actor id="porch" show=true position="right"
Mara: We're both on stage.
goto __end` },
  { cat: "Actors", title: "Custom size & opacity", code:
`scene actor_size
actor id="mara" show=true position="center" width=0.4 height=0.7 opacity=0.85
Mara: A little smaller and softer.
goto __end` },

  // ── Effects ─────────────────────────────────────────────────────────────
  { cat: "Effects", title: "Fade to black / clear", code:
`scene fx_fade
Mara: Lights out.
fade to="black" duration=0.8
fade to="clear" duration=0.8
Mara: ...and back.
goto __end` },
  { cat: "Effects", title: "Dim", code:
`scene fx_dim
dim alpha=0.5 duration=0.5
Mara: A shadow falls.
dim alpha=0 duration=0.5
goto __end` },
  { cat: "Effects", title: "Flash", code:
`scene fx_flash
flash color="white" duration=0.2
Thunder cracked.
goto __end` },
  { cat: "Effects", title: "Warm / cold tint", code:
`scene fx_tint
tint color="warm" alpha=0.3 duration=0.6
Mara: Cozy.
tint color="cold" alpha=0.3 duration=0.6
Mara: ...or not.
goto __end` },
  { cat: "Effects", title: "Blur", code:
`scene fx_blur
blur alpha=0.6 duration=0.5
Everything went soft.
blur alpha=0 duration=0.5
goto __end` },

  // ── Camera ──────────────────────────────────────────────────────────────
  { cat: "Camera", title: "Shake", code:
`scene cam_shake
camera action="shake" amplitude=10 duration=0.5
The ground trembled.
goto __end` },
  { cat: "Camera", title: "Zoom", code:
`scene cam_zoom
camera action="zoom" factor=1.3 duration=0.6
Mara: Closer now.
camera action="reset" duration=0.6
goto __end` },
  { cat: "Camera", title: "Pan + reset", code:
`scene cam_pan
camera action="pan" x=0.15 y=0 duration=0.6
The view drifts right.
camera action="reset" duration=0.6
goto __end` },

  // ── Weather ─────────────────────────────────────────────────────────────
  { cat: "Weather", title: "Rain on / off", code:
`scene weather_rain
particles type="rain" on=true
Rain ticked on the roof.
particles type="rain" on=false
Then silence.
goto __end` },
  { cat: "Weather", title: "Snow", code:
`scene weather_snow
particles type="snow" on=true
Snow drifted past the window.
goto __end` },

  // ── Audio ───────────────────────────────────────────────────────────────
  { cat: "Audio", title: "Play music", code:
`scene audio_music
audio channel="music" url="/content/audio/theme.mp3" action="play"
Mara: The melody returns.
goto __end` },
  { cat: "Audio", title: "Ambient + one-shot", code:
`scene audio_layers
audio channel="ambient" url="/content/audio/rain.mp3" action="play"
audio channel="sfx" url="/content/audio/door.mp3" action="play"
A door creaked over the rain.
goto __end` },
  { cat: "Audio", title: "Stop with fade", code:
`scene audio_stop
audio channel="music" url="/content/audio/theme.mp3" action="play"
Mara: Time to go quiet.
audio channel="music" action="stop" fade=1.0
goto __end` },

  // ── Timing ──────────────────────────────────────────────────────────────
  { cat: "Timing", title: "Wait (beat)", code:
`scene timing_wait
Mara: Listen...
wait ms=1200
Mara: ...did you hear that?
goto __end` },
  { cat: "Timing", title: "Text pace", code:
`scene timing_pace
text_pace cps=12
Mara: This... reveals... slowly.
text_pace cps=40
Mara: And this is quicker.
goto __end` },

  // ── UI & hotspots ───────────────────────────────────────────────────────
  { cat: "UI", title: "Hint", code:
`scene ui_hint
hint text="Some doors don't open twice." show=true
Mara: Choose carefully.
goto __end` },
  { cat: "UI", title: "Clickable object", code:
`scene ui_hotspot
bg id="porch"
obj id="lantern" sprite_url="/content/obj/lantern.png" x=0.5 y=0.6 width=0.2 on_click="took"
Tap the lantern.
:took
You picked up the lantern.
goto __end` },

  // ── Text magic ──────────────────────────────────────────────────────────
  { cat: "Text", title: "Variable interpolation", code:
`scene text_interp
set key="player" value="Alex"
Mara: Welcome, {player}.
Mara: Hope {player} brought a coat.
goto __end` },
  { cat: "Text", title: "Sequence {a|b|c}", code:
`scene text_seq
:knock
Mara: {First knock.|Second knock.|She stopped knocking.}
- Knock again -> knock
- Give up -> __end` },
  { cat: "Text", title: "Cycle {&a|b}", code:
`scene text_cycle
:wave
Mara: {&Hi!|Hello again!|Still you!}
- Wave back -> wave
- Leave -> __end` },
  { cat: "Text", title: "Once-only {!a}", code:
`scene text_once
:look
Narrator: {!You notice a key on the floor.}
- Look again -> look
- Pick it up -> __end` },
  { cat: "Text", title: "Conditional {cond: a|b}", code:
`scene text_cond
set key="met" value=true
Mara: {met: Good to see you again.|Have we met?}
goto __end` },
];
