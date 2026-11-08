using System.Runtime.CompilerServices;

// The core keeps its types internal, as they were before the split; the
// Windows app and the smoke tests are its only consumers.
[assembly: InternalsVisibleTo("AsteriaScale")]
[assembly: InternalsVisibleTo("AsteriaScale.Tests")]
