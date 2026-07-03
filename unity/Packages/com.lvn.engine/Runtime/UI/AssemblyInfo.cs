using System.Runtime.CompilerServices;

// VnStage exposes internal static helpers (PlacementFrom, ParseColor,
// ParseTransition, AxesFrom, ReservedActorFields) that the EditMode tests drive
// directly without spinning up a UIDocument.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
// The PlayMode smokes drive internal commit paths (ConfirmInput) on a live stage.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
