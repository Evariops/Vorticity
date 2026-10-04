using System.Runtime.CompilerServices;

// Stack buffers are not zeroed on entry. Every stackalloc in this assembly is written before it is
// read (the table builders write every cell they later read; the readers clear what they count on),
// and zeroing them was measurable on small frames, where the tables are rebuilt for every block.
[module: SkipLocalsInit]
