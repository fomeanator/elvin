using System.Runtime.CompilerServices;

// The engine test assemblies exercise internal screen seams (NovelShell's
// chapter picking, the ScreenUi helpers) without widening the public API —
// the same grant the UI core and the services package make.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
