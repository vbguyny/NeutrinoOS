// NeutrinoOS korlib - System.IO exceptions
//
// Phase 4: the exception types System.IO uses. The official BCL derives
// these from SystemException (via IOException for the file-system ones);
// NeutrinoOS keeps the IOException-family hierarchy that console
// applications catch in practice. Serialization and the full
// constructor/property surface (HResult, file name accessors) are
// reduced to message-carrying constructors.

namespace System.IO;

/// <summary>
/// The exception thrown when an I/O error occurs. NeutrinoOS raises this
/// for failures that are not more specific (bad encoding data, short
/// reads, driver errors).
/// </summary>
public class IOException : SystemException
{
    /// <summary>Initializes a new IOException without a message.</summary>
    public IOException() : base("I/O error") { }

    /// <summary>Initializes a new IOException with the specified message.</summary>
    public IOException(string message) : base(message) { }

    /// <summary>Initializes a new IOException with a message and inner exception.</summary>
    public IOException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The exception thrown when an attempt to access a file that does not
/// exist on disk fails. Raised by the System.IO.File methods that require
/// an existing file (ReadAll*, Open with FileMode.Open, Delete, ...).
/// </summary>
public class FileNotFoundException : IOException
{
    /// <summary>Initializes a new FileNotFoundException without a message.</summary>
    public FileNotFoundException() : base("File not found") { }

    /// <summary>Initializes a new FileNotFoundException with the specified message.</summary>
    public FileNotFoundException(string message) : base(message) { }
}

/// <summary>
/// The exception thrown when part of a file or directory path cannot be
/// found. NeutrinoOS raises this from the directory methods when the path
/// or one of its parents does not exist.
/// </summary>
public class DirectoryNotFoundException : IOException
{
    /// <summary>Initializes a new DirectoryNotFoundException without a message.</summary>
    public DirectoryNotFoundException() : base("Directory not found") { }

    /// <summary>Initializes a new DirectoryNotFoundException with the specified message.</summary>
    public DirectoryNotFoundException(string message) : base(message) { }
}

/// <summary>
/// The exception thrown at the end of a stream. NeutrinoOS's buffered
/// readers signal end-of-data by returning results, so this type exists
/// mainly for API compatibility with the official BCL.
/// </summary>
public class EndOfStreamException : IOException
{
    /// <summary>Initializes a new EndOfStreamException.</summary>
    public EndOfStreamException() : base("End of stream") { }
}
