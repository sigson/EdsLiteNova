namespace Eds.Core.Exceptions;

/// <summary>
/// Base of the EDS exception hierarchy. Named <c>EdsException</c> rather than
/// <c>ApplicationException</c> to avoid clashing with <see cref="System.ApplicationException"/>.
/// Mirrors <c>com.sovworks.eds.exceptions.ApplicationException</c>.
/// </summary>
public class EdsException : Exception
{
    public EdsException() { }
    public EdsException(string message) : base(message) { }
    public EdsException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The supplied password (and/or key files) did not open the container.</summary>
public class WrongPasswordException : EdsException
{
    public WrongPasswordException() : base("Wrong password or key file.") { }
    public WrongPasswordException(string message) : base(message) { }
}

/// <summary>The data does not look like a container of the expected format.</summary>
public class WrongFileFormatException : EdsException
{
    public WrongFileFormatException() : base("Wrong file format.") { }
    public WrongFileFormatException(string message) : base(message) { }
}

/// <summary>The container header version is outside the supported range.</summary>
public class WrongContainerVersionException : EdsException
{
    public WrongContainerVersionException() : base("Unsupported container version.") { }
    public WrongContainerVersionException(string message) : base(message) { }
}

/// <summary>A CRC in the container header did not match (corrupt or wrong key).</summary>
public class HeaderCrcException : EdsException
{
    public HeaderCrcException() : base("Header CRC mismatch.") { }
    public HeaderCrcException(string message) : base(message) { }
}

/// <summary>The container type is recognised but not supported.</summary>
public class UnsupportedContainerTypeException : EdsException
{
    public UnsupportedContainerTypeException() : base("Unsupported container type.") { }
    public UnsupportedContainerTypeException(string message) : base(message) { }
}

/// <summary>A user- or token-driven cancellation of a long operation.</summary>
public class OperationCancelledException : EdsException
{
    public OperationCancelledException() : base("Operation cancelled.") { }
    public OperationCancelledException(string message) : base(message) { }
}

/// <summary>The user aborted the operation.</summary>
public class UserAbortException : EdsException
{
    public UserAbortException() : base("Operation aborted by user.") { }
    public UserAbortException(string message) : base(message) { }
}

/// <summary>A failure originating in the native crypto layer.</summary>
public class NativeError : EdsException
{
    public NativeError(string message) : base(message) { }
}

/// <summary>Base for encryption-engine level errors (mirrors EncryptionEngineException).</summary>
public class EncryptionEngineException : EdsException
{
    public EncryptionEngineException(string message) : base(message) { }
    public EncryptionEngineException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Generic encrypt/decrypt error (mirrors EncDecException).</summary>
public class EncDecException : EdsException
{
    public EncDecException(string message) : base(message) { }
    public EncDecException(string message, Exception inner) : base(message, inner) { }
}
