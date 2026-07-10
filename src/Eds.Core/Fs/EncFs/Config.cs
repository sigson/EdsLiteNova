using System.Xml;
using System.Xml.Linq;
using Eds.Core.Fs.EncFs.Codecs;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// EncFS volume configuration (<c>encfs6.xml</c>). Faithful port of
/// <c>fs.encfs.Config</c>. Parses/writes the boost-serialization XML, resolving
/// the data and name codecs against <see cref="EncFsCodecs"/>. Holds the volume
/// parameters (key size, block size, IV/MAC options) plus the encrypted volume
/// key and salt used later to derive the master key from the password.
///
/// The Android/DOM plumbing is replaced with <see cref="System.Xml.Linq"/>; the
/// element/attribute names and the algorithm matching (name + version) are kept
/// exactly for on-disk compatibility.
/// </summary>
public sealed class Config
{
    public const string ConfigFileName2 = "encfs6.xml";
    public const string ConfigFileName = "." + ConfigFileName2;

    private string? _creator;
    private int _subVersion;
    private IDataCodecInfo? _dataCipher;
    private INameCodecInfo? _nameCipher;
    private int _keySizeBits;
    private int _blockSize;
    private byte[]? _keyData;
    private byte[]? _salt;
    private int _kdfIterations;
    private int _desiredKdfDuration;
    private int _blockMacBytes;
    private int _blockMacRandBytes;
    private bool _uniqueIV;
    private bool _externalIVChaining;
    private bool _chainedNameIV;
    private bool _allowHoles;

    // ---- reading -------------------------------------------------------

    /// <summary>Locates the config file (.encfs6.xml or encfs6.xml) inside a directory.</summary>
    public static IFile? GetConfigFile(IDirectory dir)
    {
        var p = dir.Path.Combine(ConfigFileName);
        if (p.Exists() && p.IsFile()) return p.GetFile();
        p = dir.Path.Combine(ConfigFileName2);
        return p.Exists() && p.IsFile() ? p.GetFile() : null;
    }

    /// <summary>Reads the config from the encfs6.xml inside the given root folder.</summary>
    public void Read(IPath rootFolder)
    {
        var cfgFile = GetConfigFile(rootFolder.GetDirectory())
                      ?? throw new InvalidDataException("EncFS config file doesn't exist");
        using var s = cfgFile.GetInputStream();
        Read(s);
    }

    /// <summary>Writes the config to .encfs6.xml inside the given root folder.</summary>
    public void Write(IPath rootFolder)
    {
        var f = rootFolder.Combine(ConfigFileName).GetFile();
        using var s = f.GetOutputStream();
        Write(s);
    }

    public void Read(Stream config)
    {
        XDocument doc;
        try
        {
            // Real encfs6.xml carries a <!DOCTYPE boost_serialization>; the default
            // XDocument.Load prohibits DTDs, so read via an explicit reader that
            // ignores the DTD (with no external resolver, so it is safe).
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(config, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception e) { throw new InvalidDataException("Failed reading the config file", e); }

        var cfg = doc.Descendants("cfg").FirstOrDefault()
                  ?? throw new InvalidDataException("cfg element not found");
        ReadCfgElement(cfg);
    }

    private void ReadCfgElement(XElement cfg)
    {
        _subVersion = GetParam(cfg, "version", 20100713);
        _creator = GetParam(cfg, "creator", "");
        _dataCipher = (IDataCodecInfo?)LoadAlgInfo(cfg, "cipherAlg", EncFsCodecs.SupportedDataCodecs, null);
        _nameCipher = (INameCodecInfo?)LoadAlgInfo(cfg, "nameAlg", EncFsCodecs.SupportedNameCodecs, null);
        _keySizeBits = GetParam(cfg, "keySize", 0);
        _blockSize = GetParam(cfg, "blockSize", 0);
        _uniqueIV = GetParam(cfg, "uniqueIV", true);
        _chainedNameIV = GetParam(cfg, "chainedNameIV", true);
        _externalIVChaining = GetParam(cfg, "externalIVChaining", false);
        _blockMacBytes = GetParam(cfg, "blockMACBytes", 0);
        _blockMacRandBytes = GetParam(cfg, "blockMACRandBytes", 0);
        _allowHoles = GetParam(cfg, "allowHoles", true);

        _keyData = GetBytes(cfg, "encodedKeyData");
        int size = GetParam(cfg, "encodedKeySize", 0);
        if (size > 0 && (_keyData == null || size != _keyData.Length))
            throw new InvalidDataException("Failed decoding key data");

        _salt = GetBytes(cfg, "saltData");
        size = GetParam(cfg, "saltLen", 0);
        if (size > 0 && (_salt == null || size != _salt.Length))
            throw new InvalidDataException("Failed decoding salt data");

        _kdfIterations = GetParam(cfg, "kdfIterations", 0);
        _desiredKdfDuration = GetParam(cfg, "desiredKDFDuration", 0);
    }

    private static string? GetParam(XElement cfg, string paramName, string? defaultValue)
    {
        var n = cfg.Descendants(paramName).FirstOrDefault();
        return n == null ? defaultValue : n.Value;
    }

    private static int GetParam(XElement cfg, string paramName, int defaultValue)
    {
        var s = GetParam(cfg, paramName, (string?)null);
        return s == null ? defaultValue : int.Parse(s.Trim());
    }

    private static bool GetParam(XElement cfg, string paramName, bool defaultValue)
    {
        var s = GetParam(cfg, paramName, (string?)null);
        return s == null ? defaultValue : s.Trim() != "0";
    }

    private static byte[]? GetBytes(XElement cfg, string paramName)
    {
        var encoded = GetParam(cfg, paramName, (string?)null);
        // Convert.FromBase64String tolerates embedded whitespace/newlines.
        return encoded == null ? null : Convert.FromBase64String(encoded.Trim());
    }

    private IAlgInfo? LoadAlgInfo(XElement cfg, string paramName, IEnumerable<IAlgInfo> supported, IAlgInfo? def)
    {
        var n = cfg.Descendants(paramName).FirstOrDefault();
        if (n == null) return def;
        string? algName = GetParam(n, "name", (string?)null)
                          ?? throw new InvalidDataException("Name is not specified for " + paramName);
        int major = GetParam(n, "major", 0);
        int minor = GetParam(n, "minor", 0);
        foreach (var info in supported)
            if (algName == info.Name && info.Version1 >= major && info.Version2 >= minor)
                return info.Select(this);
        throw new InvalidDataException($"Unsupported algorithm: {algName} major={major} minor={minor}");
    }

    // ---- writing -------------------------------------------------------

    public void Write(Stream output)
    {
        var cfg = new XElement("cfg",
            new XAttribute("class_id", "0"),
            new XAttribute("tracking_level", "0"),
            new XAttribute("version", "20"),
            new XElement("version", _subVersion),
            new XElement("creator", _creator ?? ""),
            MakeAlgInfoElement("cipherAlg", _dataCipher!),
            MakeAlgInfoElement("nameAlg", _nameCipher!),
            new XElement("keySize", _keySizeBits),
            new XElement("blockSize", _blockSize),
            new XElement("uniqueIV", _uniqueIV ? "1" : "0"),
            new XElement("chainedNameIV", _chainedNameIV ? "1" : "0"),
            new XElement("externalIVChaining", _externalIVChaining ? "1" : "0"),
            new XElement("blockMACBytes", _blockMacBytes),
            new XElement("blockMACRandBytes", _blockMacRandBytes),
            new XElement("allowHoles", _allowHoles ? "1" : "0"),
            new XElement("encodedKeySize", _keyData?.Length ?? 0),
            new XElement("encodedKeyData", _keyData == null ? "" : Convert.ToBase64String(_keyData)),
            new XElement("saltLen", _salt?.Length ?? 0),
            new XElement("saltData", _salt == null ? "" : Convert.ToBase64String(_salt)),
            new XElement("kdfIterations", _kdfIterations),
            new XElement("desiredKDFDuration", _desiredKdfDuration));

        var root = new XElement("boost_serialization",
            new XAttribute("signature", "serialization::archive"),
            new XAttribute("version", "14"),
            cfg);

        new XDocument(root).Save(output);
    }

    private static XElement MakeAlgInfoElement(string paramName, IAlgInfo info)
        => new(paramName,
            new XElement("name", info.Name),
            new XElement("major", info.Version1),
            new XElement("minor", info.Version2));

    // ---- defaults (new volume) ----------------------------------------

    public void InitNew(string? creator = "EDS")
    {
        _creator = creator ?? "EDS";
        _subVersion = 20100713;
        _kdfIterations = 100000;
        _desiredKdfDuration = 500;
        _keySizeBits = 192;
        _blockSize = 1024;
        _blockMacRandBytes = _blockMacBytes = 0;
        _uniqueIV = true;
        _chainedNameIV = true;
        _externalIVChaining = false;
        _allowHoles = true;
        _dataCipher = (IDataCodecInfo)new AesDataCodecInfo().Select(this);
        _nameCipher = (INameCodecInfo)new BlockNameCodecInfo().Select(this);
    }

    // ---- accessors -----------------------------------------------------

    public IDataCodecInfo? GetDataCodecInfo() => _dataCipher;
    public void SetDataCodecInfo(IDataCodecInfo info) => _dataCipher = info;
    public INameCodecInfo? GetNameCodecInfo() => _nameCipher;
    public void SetNameCodecInfo(INameCodecInfo info) => _nameCipher = info;

    public int KeySize => _keySizeBits / 8;
    public void SetKeySize(int numBytes) => _keySizeBits = numBytes * 8;

    public int BlockSize => _blockSize;
    public void SetBlockSize(int val) => _blockSize = val;

    public byte[]? Salt { get => _salt; set => _salt = value; }
    public byte[]? EncryptedVolumeKey { get => _keyData; set => _keyData = value; }

    public int KdfIterations { get => _kdfIterations; set => _kdfIterations = value; }
    public int DesiredKdfDuration => _desiredKdfDuration;

    public bool UseUniqueIV { get => _uniqueIV; set => _uniqueIV = value; }
    public bool UseChainedNameIV { get => _chainedNameIV; set => _chainedNameIV = value; }
    public bool UseExternalFileIV { get => _externalIVChaining; set => _externalIVChaining = value; }
    public bool AllowHoles { get => _allowHoles; set => _allowHoles = value; }

    public int MacBytes { get => _blockMacBytes; set => _blockMacBytes = value; }
    public int MacRandBytes { get => _blockMacRandBytes; set => _blockMacRandBytes = value; }

    public string? Creator => _creator;
    public int SubVersion => _subVersion;
}
