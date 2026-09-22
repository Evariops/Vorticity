using System.Diagnostics.CodeAnalysis;

// The row vectors check the experimental row encoding byte for byte, so the test assembly is
// experimental itself: its uses of the package need no suppression, and nothing references it.
[assembly: Experimental("VXCONFORMANCE")]
