namespace Eds.Core.Containers;

// NOTE ON LOCATION: this file physically lives in the Eds.Core assembly (not
// Eds.Core.Containers) even though it keeps the Eds.Core.Containers namespace.
// The opening-progress contract is shared by two layers — the container reader
// (Eds.Core.Containers) and the locations layer (Eds.Core.Locations, which
// IOpenableLocation exposes it from). Placing the type in Core lets both depend
// on it without Core taking a reference on the Containers project (that would be
// a cycle, since Containers references Core). The namespace is preserved so every
// existing reference — in the Containers, MAUI and console projects — keeps
// compiling unchanged.

/// <summary>
/// Reports container-opening progress. Replaces the original
/// <c>ContainerOpeningProgressReporter</c>: the KDF percentage flows through
/// <see cref="Progress"/> and cancellation through <see cref="CancellationToken"/>,
/// while the current algorithm names are surfaced for UI ("trying AES / SHA-512").
/// </summary>
public interface IContainerOpeningProgressReporter
{
    IProgress<int>? Progress { get; }
    CancellationToken CancellationToken { get; }
    void SetCurrentKdfName(string name);
    void SetCurrentEncryptionAlgName(string name);
}

/// <summary>Convenience reporter usable from console/tests.</summary>
public sealed class DelegateProgressReporter : IContainerOpeningProgressReporter
{
    public IProgress<int>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
    public Action<string>? OnKdf { get; init; }
    public Action<string>? OnCipher { get; init; }
    public void SetCurrentKdfName(string name) => OnKdf?.Invoke(name);
    public void SetCurrentEncryptionAlgName(string name) => OnCipher?.Invoke(name);
}
