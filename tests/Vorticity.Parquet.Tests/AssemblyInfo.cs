using System.Diagnostics.CodeAnalysis;

// The tests exercise an experimental package, so the test assembly is experimental itself: its uses
// of it need no suppression, and nothing references it.
[assembly: Experimental("VXPARQUETTESTS")]
