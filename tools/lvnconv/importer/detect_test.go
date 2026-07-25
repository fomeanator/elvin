package importer

import "testing"

func TestDetectAliasCollisionsFindsRosterOnlyAlias(t *testing.T) {
	// "ГГ" never speaks (no SpeakerDetect entry) but IS in the roster with
	// art — the Soviet novel's actual shape: the protagonist's portrait is
	// registered under a short nickname the dialogue never uses as `who`.
	speakers := []SpeakerDetect{
		{Who: "Главный герой", HasArt: false, Role: "protagonist"},
		{Who: "Timur", HasArt: true, Role: "npc"},
	}
	cast := map[string]string{
		"ГГ":    "Olesya.png",
		"Timur": "timur.png",
	}
	collisions := detectAliasCollisions(speakers, cast)

	found := false
	for _, c := range collisions {
		if (c.A == "Главный герой" && c.B == "ГГ") || (c.A == "ГГ" && c.B == "Главный герой") {
			found = true
			if c.Reason != "initials-match" {
				t.Errorf("expected initials-match, got %q", c.Reason)
			}
		}
	}
	if !found {
		t.Fatalf("expected a Главный герой/ГГ collision, got %+v", collisions)
	}
}

func TestDetectAliasCollisionsIgnoresSameArtStatus(t *testing.T) {
	speakers := []SpeakerDetect{
		{Who: "Тимур", HasArt: true},
		{Who: "Тимур2", HasArt: true}, // both have art — not a collision candidate
	}
	if got := detectAliasCollisions(speakers, map[string]string{}); len(got) != 0 {
		t.Errorf("same-art-status pair should never collide, got %+v", got)
	}
}

func TestDetectAliasCollisionsNoFalsePositiveOnUnrelatedNames(t *testing.T) {
	speakers := []SpeakerDetect{{Who: "Александр", HasArt: false}}
	cast := map[string]string{"Мария": "maria.png"}
	if got := detectAliasCollisions(speakers, cast); len(got) != 0 {
		t.Errorf("unrelated names must not collide, got %+v", got)
	}
}

func TestInitialsAndLooseContains(t *testing.T) {
	if got := initials("Главный герой"); got != "ГГ" {
		t.Errorf("initials(%q) = %q, want ГГ", "Главный герой", got)
	}
	if !looseContains("Matvey_black", "Matvey") {
		t.Error("looseContains should match a variant-suffixed name against its base")
	}
	if looseContains("ab", "ab") {
		t.Error("looseContains should reject names shorter than 3 runes")
	}
}
