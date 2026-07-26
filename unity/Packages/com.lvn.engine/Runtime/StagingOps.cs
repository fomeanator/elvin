using System.Collections.Generic;

namespace Lvn
{
    /// <summary>
    /// Every op the <c>.lvn</c> language defines — the C# mirror of the reference
    /// registry <c>lvn.KnownOps</c> (<c>tools/lvnconv/lvn/validate.go</c>), which is
    /// also the row set of <c>/conformance/ops-owners.json</c>.
    ///
    /// <para>Despite the type name (kept since 0.1.0 — hosts compile against it),
    /// this is NOT "the ops that reach the stage": flow ops like <c>goto</c> and
    /// <c>if</c> are in here too and never leave <see cref="LvnPlayer"/>. The set
    /// answers exactly one question — <i>is this name part of the language, or is it
    /// mine?</i> — which is what a host needs inside
    /// <see cref="ILvnStage.ApplyStage"/>: an op it does not draw but that IS listed
    /// here is an engine-side gap worth logging, while one that is NOT listed is
    /// either the host's own op (see <see cref="LvnOps"/>) or a typo the validator
    /// should have rejected. Which ops actually arrive at a stage is stated per op
    /// in <c>/conformance/ops-owners.json</c>, not here.</para>
    ///
    /// <para>Membership is not a promise that a bare <c>com.lvn.engine</c> install
    /// handles the op: <c>wardrobe_show</c> is part of the language and is
    /// implemented in <c>com.lvn.engine.shell</c>. The owning package is, again, a
    /// column of the ownership table.</para>
    ///
    /// <para>The mirror is checked, not trusted. It fell six ops behind the language
    /// once, silently, because nothing dispatches on it. Two tests now diff it
    /// against the source of truth: <c>TestCSharpKnownOpsMirrorKnownOps</c>
    /// (<c>tools/lvnconv/lvn/conformance_test.go</c>) reads this very list out of the
    /// file and compares it with <c>lvn.KnownOps</c> — no Unity needed — and
    /// <c>CameraRigTests.StagingOpsMatchTheSharedOpTable</c> compares it with the
    /// ownership table in EditMode. Adding an op to the language and not to this set
    /// is a red build, so keep this list in step; the grouping below is a reader's
    /// aid and carries no meaning.</para>
    /// </summary>
    public static class StagingOps
    {
        public static readonly HashSet<string> Known = new HashSet<string>
        {
            // flow and state — consumed by LvnPlayer, never forwarded
            "say", "choice", "label", "goto", "if", "call", "return",
            "set", "inc",
            // paused or acted on by the player AND forwarded to the stage
            "wait", "input", "preload", "load",
            // presentation — forwarded to ILvnStage.ApplyStage
            "bg", "actor", "obj", "clear", "text", "audio",
            "fade", "dim", "flash", "tint", "blur",
            "camera", "particles", "anim", "text_pace", "hint", "save",
            // part of the language, implemented in com.lvn.engine.shell
            "wardrobe_show",
        };
    }
}
