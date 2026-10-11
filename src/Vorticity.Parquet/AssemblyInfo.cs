using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

[assembly: Experimental("VX0003", Message = "Vorticity.Parquet's API may change between releases.")]

// The kernels take their scratch from the stack and write every byte they read back; zeroing it
// first would be work no result depends on.
[module: SkipLocalsInit]
