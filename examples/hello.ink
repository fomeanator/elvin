// hello.ink — the smallest LVN story. Open it in Inky to play-test the words;
// compile it with `lvnconv convert -i hello.ink -o hello.lvn` to get the
// container the runtime plays. The `# tag:` lines are staging Inky ignores.

# scene: hello
# actors: Mara=mara

-> porch

== porch ==
# bg: porch sprite_url=/content/bg/porch.jpg
# fade: clear 0.6
Rain ticked on the porch roof.
# actor: mara show=true position=left sprite_url=/content/actors/mara.png
Mara: You came back.
Mara: I wasn't sure you would.

* [I did.]
    ~ warmth = warmth + 1
    Mara [smile]: Then come in out of the rain.
    -> inside
* [I can't stay.] -> leave

== inside ==
# particles: rain off
Mara: The kettle's still warm.
-> END

== leave ==
# hint: Some doors don't open twice.
Mara: She watched the dark take you back.
# fade: black 0.8
-> END
