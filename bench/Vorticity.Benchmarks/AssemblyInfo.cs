using System.Diagnostics.CodeAnalysis;

// The benchmarks measure the two experimental packages too, so the host is experimental itself:
// its uses of them need no suppression, and nothing references it.
[assembly: Experimental("VXBENCH")]
