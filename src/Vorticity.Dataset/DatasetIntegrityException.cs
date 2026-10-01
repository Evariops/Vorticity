using System;

namespace Vorticity.Dataset;

/// <summary>
/// A guard found that a change would lose rows, double them or lose their order, and the change
/// was not committed: a compaction whose outputs do not hold its inputs' rows, a delete whose marks
/// or rewrite do not take the rows its count found, an object meant to be in key order that is not.
/// </summary>
/// <remarks>
/// It says the library, or an object it read, is at fault, not that another writer went first: the
/// same change run again meets it again. The objects the change wrote before the guard tripped are
/// referenced by no version, and vacuum takes them once they leave the retention window.
/// </remarks>
public sealed class DatasetIntegrityException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public DatasetIntegrityException()
        : base("A change to the dataset would have lost or doubled rows, and was not committed.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which change, which object, and the counts that disagree.</param>
    public DatasetIntegrityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">Which change, which object, and the counts that disagree.</param>
    /// <param name="innerException">What the guard caught.</param>
    public DatasetIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
