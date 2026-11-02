using System.Diagnostics.CodeAnalysis;

// The transport runs the dataset's cluster, whose API is experimental (VX0001): its frames and its own API change with it.
[assembly: Experimental("VX0003", Message = "The cluster's frames and API may change between releases, as the dataset's.")]
