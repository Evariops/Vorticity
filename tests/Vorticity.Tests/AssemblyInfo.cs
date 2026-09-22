using System.Diagnostics.CodeAnalysis;

// The tests exercise the two experimental packages, so the test assembly is experimental itself:
// its uses of them need no suppression, and nothing references it.
[assembly: Experimental("VXTESTS")]
