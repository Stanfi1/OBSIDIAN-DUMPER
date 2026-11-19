using System.Globalization;

namespace Obsidian.Dumper.Core.Inspection;

public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
}

public enum ProtectionSeverity
{
    Info,
    Suspicious,
    Critical,
}

public sealed class AssemblySummary
{
    public string Name { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Culture { get; init; } = string.Empty;
    public string PublicKeyToken { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = "DLL";
    public string? EntryPoint { get; init; }
    public string? TargetFramework { get; init; }
    public bool IsStrongNamed { get; init; }
    public bool IsIlOnly { get; init; }
    public bool IsMixedMode { get; init; }
    public bool IsReadyToRun { get; init; }
    public string Machine { get; init; } = "x64";
    public string Subsystem { get; init; } = "windows";
    public DateTime? CompileTimestamp { get; init; }
    public string? Mvid { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Description { get; init; }
    public string? Copyright { get; init; }
    public string? FileVersion { get; init; }

    public int TypeCount { get; init; }
    public int MethodCount { get; init; }
    public int FieldCount { get; init; }
    public int PropertyCount { get; init; }
    public int EventCount { get; init; }
    public int NamespaceCount { get; init; }
    public int ResourceCount { get; init; }
    public int AssemblyRefCount { get; init; }
    public int TypeRefCount { get; init; }
    public int MemberRefCount { get; init; }
    public int CustomAttributeCount { get; init; }
    public int IlBytes { get; init; }

    public long FileSize { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string? SourcePath { get; init; }

    /// <summary>
    /// Non-fatal problems found while reading a damaged or protected module.
    /// Null/empty for healthy assemblies.
    /// </summary>
    public string? Diagnostics { get; init; }
    public IReadOnlyList<PeSectionInfo> Sections { get; init; } = Array.Empty<PeSectionInfo>();
    public IReadOnlyList<ProtectionHint> Protections { get; init; } = Array.Empty<ProtectionHint>();
    public IReadOnlyList<ReferenceEntry> References { get; init; } = Array.Empty<ReferenceEntry>();
    public IReadOnlyList<ResourceEntry> Resources { get; init; } = Array.Empty<ResourceEntry>();

    public string SizeLabel => FormatBytes(FileSize);

    public string IdentityLabel => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";

    public bool IsProtected => Protections.Any(p => p.Severity != ProtectionSeverity.Info);
    public bool HasDiagnostics => !string.IsNullOrEmpty(Diagnostics);

    public static string FormatBytes(long bytes)
    {
        bool negative = bytes < 0;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = negative ? -(double)bytes : bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string sign = negative ? "-" : string.Empty;
        return unit == 0
            ? $"{sign}{(long)value} B"
            : $"{sign}{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

public sealed class PeSectionInfo
{
    public string Name { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public uint VirtualSize { get; init; }
    public uint RawSize { get; init; }
    public uint Characteristics { get; init; }
    public double Entropy { get; init; }

    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
    public bool IsWritable => (Characteristics & 0x80000000) != 0;
    public bool IsReadable => (Characteristics & 0x40000000) != 0;
    public string FlagsLabel
    {
        get
        {
            var flags = new List<string>(3);
            if (IsReadable) flags.Add("r");
            if (IsWritable) flags.Add("w");
            if (IsExecutable) flags.Add("x");
            return flags.Count == 0 ? "-" : string.Join(string.Empty, flags);
        }
    }

    public string SizeLabel => AssemblySummary.FormatBytes(RawSize);
    public string EntropyLabel => Entropy.ToString("0.00", CultureInfo.InvariantCulture);
    public double EntropyPercent => Math.Round(Entropy / 8.0 * 100, 1);
}

public sealed class ProtectionHint
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ProtectionSeverity Severity { get; init; }
    public string Evidence { get; init; } = string.Empty;

    public string SeverityLabel => Severity switch
    {
        ProtectionSeverity.Critical => "critical",
        ProtectionSeverity.Suspicious => "suspicious",
        _ => "info",
    };
}

public sealed class ReferenceEntry
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Culture { get; init; } = string.Empty;
    public string PublicKeyToken { get; init; } = string.Empty;
    public string Kind { get; init; } = "assembly";
    public int UsageCount { get; init; }

    public string Identity => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
    public string TokenLabel => string.IsNullOrEmpty(PublicKeyToken) || PublicKeyToken == "null" ? "—" : PublicKeyToken;
    public bool IsFramework => Name == "mscorlib" || Name == "netstandard" ||
                               Name is "System" or "WindowsBase" or "PresentationCore" or "PresentationFramework" ||
                               Name.StartsWith("System.", StringComparison.Ordinal) ||
                               Name.StartsWith("Microsoft.", StringComparison.Ordinal);
}

public sealed class ResourceEntry
{
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Kind { get; init; } = "embedded";
    public bool IsPublic { get; init; }

    public string SizeLabel => AssemblySummary.FormatBytes(Size);
}

public sealed class StringEntry
{
    public string Value { get; init; } = string.Empty;
    public string Kind { get; init; } = "user";
    public int Count { get; init; }

    public string Display => Value.Replace("\\", "\\\\")
                                  .Replace("\r", "\\r")
                                  .Replace("\n", "\\n")
                                  .Replace("\t", "\\t")
                                  .Replace("\0", "\\0");
    public int Length => Value.Length;
}

public sealed class TypeEntry
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? BaseType { get; init; }
    public TypeKind Kind { get; init; }
    public string Visibility { get; init; } = "public";
    public int MethodCount { get; init; }
    public int FieldCount { get; init; }
    public int PropertyCount { get; init; }
    public int NestedLevel { get; init; }
    public int Token { get; init; }
    public bool IsCompilerGenerated { get; init; }
    public bool IsObfuscated { get; init; }
    public int TotalMethodCount { get; init; }
    public int IlSize { get; init; }
    public IReadOnlyList<MethodEntry> Methods { get; init; } = Array.Empty<MethodEntry>();

    public string NamespaceLabel => string.IsNullOrEmpty(Namespace) ? "<global>" : Namespace;
    public string KindLabel => Kind.ToString().ToLowerInvariant();
    public string Indent => new(' ', NestedLevel * 2);
    public double IndentPixels => NestedLevel * 12d;
    public string DisplayName => new string(' ', NestedLevel * 2) + Name;
    public string TokenLabel => $"0x{Token:X8}";
    public string Summary => $"{KindLabel} · {MethodCount} methods · {FieldCount} fields";
}

public sealed class MethodEntry
{
    public string Name { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string ReturnType { get; init; } = "void";
    public int Token { get; init; }
    public int Rva { get; init; }
    public int IlSize { get; init; }
    public int MaxStack { get; init; }
    public int LocalCount { get; init; }
    public bool IsStatic { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsConstructor { get; init; }
    public bool IsPInvoke { get; init; }
    public bool IsRuntime { get; init; }
    public bool HasBody { get; init; }
    public string Flags { get; init; } = string.Empty;
    public string Access { get; init; } = "public";

    public string TokenLabel => $"0x{Token:X8}";
    public string IlSizeLabel => HasBody ? $"{IlSize} B" : "—";
    public string Display => string.IsNullOrEmpty(Signature) ? Name : Signature;
}
