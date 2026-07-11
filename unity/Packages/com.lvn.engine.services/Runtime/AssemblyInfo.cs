using System.Runtime.CompilerServices;

// The engine-core test assemblies exercise internal seams here (the wallet's
// offline mirror/queue) without widening the public API — the same grant the
// core makes in its own AssemblyInfo.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
