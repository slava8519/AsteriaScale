using System.Runtime.CompilerServices;

// The core keeps its types internal, as they were before the split; its only
// consumers are the Windows app, the smoke tests (the macOS subset keeps the
// assembly name AsteriaScale.Tests) and the macOS Core host process
// (docs/MACOS.md M11).
[assembly: InternalsVisibleTo("AsteriaScale")]
[assembly: InternalsVisibleTo("AsteriaScale.Tests")]
[assembly: InternalsVisibleTo("AsteriaScale.CoreHost")]
