namespace Vorticity.Writing;

/// <summary>
/// What a file holds at the end of a group of rows a caller asked for (<see cref="VortexFileWriter.EndGroupAsync"/>): the
/// bytes its sink took, every chunk of every column written, and the rows those chunks hold. Exact, not an estimate: what a
/// dataset's job ends an object on.
/// </summary>
/// <param name="DataBytes">
/// The bytes of data the sink took: the file's header and its columns' chunks, without the indexes' runs written among them,
/// which the file counts with its indexes (<see cref="WriteBytes.Data"/>).
/// </param>
/// <param name="Rows">The rows the file's chunks hold; the rows handed over past them wait for the chunks after.</param>
internal readonly record struct GroupEnd(long DataBytes, long Rows);
