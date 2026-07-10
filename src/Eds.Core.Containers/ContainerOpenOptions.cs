namespace Eds.Core.Containers;

/// <summary>
/// Optional parameters for opening (or re-keying) a container beyond the bare
/// password: keyfiles to mix in, a VeraCrypt PIM (custom KDF iteration count), and
/// a progress reporter for the algorithm sweep. Replaces the several separate
/// arguments the Android openers threaded through.
/// </summary>
public sealed class ContainerOpenOptions
{
    /// <summary>Keyfiles to mix into the password (TrueCrypt/VeraCrypt only).</summary>
    public IReadOnlyList<Func<Stream>>? Keyfiles { get; init; }

    /// <summary>
    /// VeraCrypt PIM. 0 means "use the format default iteration count"; a positive
    /// value selects <c>15000 + PIM*1000</c> iterations. Ignored by TrueCrypt/LUKS.
    /// </summary>
    public int Pim { get; init; }

    /// <summary>Progress + cancellation for the KDF/algorithm sweep.</summary>
    public IContainerOpeningProgressReporter? Reporter { get; init; }
}
