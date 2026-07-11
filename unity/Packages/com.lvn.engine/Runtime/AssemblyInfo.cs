using System.Runtime.CompilerServices;

// The engine-core tests exercise internal seams without widening the public
// API. (The services package makes the same grant in its own AssemblyInfo.)
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
