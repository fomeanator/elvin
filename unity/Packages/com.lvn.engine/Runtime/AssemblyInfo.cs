using System.Runtime.CompilerServices;

// The engine-core tests exercise internal seams (the wallet's offline
// mirror/queue) without widening the public API.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
