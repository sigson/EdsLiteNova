namespace Eds.Core.Containers;

/// <summary>
/// Declarative capabilities of a container format, so the UI knows which fields to
/// offer (keyfiles, PIM, hidden volume) and their limits without hard-coding
/// format knowledge in the view. Port of <c>container.ContainerFormatInfo</c> +
/// the per-format <c>FormatInfo</c> classes, reduced to the metadata the port
/// currently needs. <see cref="OpenPriority"/> mirrors the order
/// <see cref="EdsContainer"/> sweeps layouts (lower = tried first).
/// </summary>
public interface IContainerFormatInfo
{
    string Name { get; }
    ContainerCreator.Format Format { get; }
    bool HasHiddenContainerSupport { get; }
    bool HasKeyfilesSupport { get; }
    bool HasCustomKdfIterationsSupport { get; } // VeraCrypt PIM
    int MaxPasswordLength { get; }
    int OpenPriority { get; }
}

/// <summary>The known container formats and their capabilities.</summary>
public static class ContainerFormats
{
    private sealed record Info(
        string Name,
        ContainerCreator.Format Format,
        bool HasHiddenContainerSupport,
        bool HasKeyfilesSupport,
        bool HasCustomKdfIterationsSupport,
        int MaxPasswordLength,
        int OpenPriority) : IContainerFormatInfo;

    public static readonly IContainerFormatInfo Luks =
        new Info("LUKS", ContainerCreator.Format.Luks, false, false, false, 512, 0);

    // Hidden-volume support is not implemented in the port (the lite original
    // disables it too), so it is reported as false even though keyfiles/PIM are
    // supported. Flip to true only once a hidden layout actually exists (§3.2).
    public static readonly IContainerFormatInfo VeraCrypt =
        new Info("VeraCrypt", ContainerCreator.Format.VeraCrypt, false, true, true, 64, 1);

    public static readonly IContainerFormatInfo TrueCrypt =
        new Info("TrueCrypt", ContainerCreator.Format.TrueCrypt, false, true, false, 64, 2);

    /// <summary>All formats, in open-sweep priority order.</summary>
    public static IReadOnlyList<IContainerFormatInfo> All { get; } =
        new[] { Luks, VeraCrypt, TrueCrypt };

    public static IContainerFormatInfo For(ContainerCreator.Format format) => format switch
    {
        ContainerCreator.Format.Luks => Luks,
        ContainerCreator.Format.VeraCrypt => VeraCrypt,
        ContainerCreator.Format.TrueCrypt => TrueCrypt,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
