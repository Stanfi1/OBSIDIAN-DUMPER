using dnlib.DotNet;

namespace Obsidian.Dumper.Core.Inspection;

/// <summary>
/// Structural protector fingerprints. Deliberately conservative: every hint is
/// evidence based, and "info" hints never imply that an assembly is malicious.
/// </summary>
public static class ProtectionAnalyzer
{
    /// <summary>
    /// Markers that only occur in the metadata of a specific protector. Generic
    /// words ("Reactor", "Enigma", "PoweredBy") were removed because ordinary
    /// applications use them too and produced false positives.
    /// </summary>
    private static readonly (string Marker, string Name)[] KnownProtectors =
    [
        ("ConfusedBy", "ConfuserEx"),
        ("ConfusedByAttribute", "ConfuserEx"),
        ("BabelAttribute", "Babel Obfuscator"),
        ("BabelObfuscatorAttribute", "Babel Obfuscator"),
        ("ObfuscatedByGoliath", "Goliath"),
        ("SmartAssembly", "SmartAssembly"),
        ("Eazfuscator", "Eazfuscator.NET"),
        ("CryptoObfuscator", "Crypto Obfuscator"),
        (".NET Reactor", "Eziriz .NET Reactor"),
        ("DotNetReactor", "Eziriz .NET Reactor"),
        ("Eziriz", "Eziriz .NET Reactor"),
        ("Dotfuscator", "Dotfuscator"),
        ("AgileDotNet", "Agile.NET"),
        ("Themida", "Themida"),
        ("VMProtect", "VMProtect"),
        ("EnigmaProtector", "Enigma Protector"),
        ("Enigma Protector", "Enigma Protector"),
        ("ILProtector", "ILProtector"),
        ("Spices.Net", "Spices.NET"),
        ("Xenocode", "Xenocode Postbuild"),
    ];

    public static IReadOnlyList<ProtectionHint> Analyze(
        ModuleDefMD module,
        IReadOnlyList<TypeEntry> types,
        IReadOnlyList<PeSectionInfo> sections,
        IReadOnlyList<ResourceEntry> resources,
        Dictionary<string, (int Count, string Kind)> rawStrings)
    {
        var hints = new List<ProtectionHint>();
        var typeNames = types.Select(t => t.FullName).ToList();
        var assemblyAttributes = SafeAttributeNames(module);

        AddKnownProtectorHints(typeNames, assemblyAttributes, hints);
        AddSuppressIldasmHint(module, assemblyAttributes, hints);
        AddNameManglingHint(types, hints);
        AddStrippedBodiesHint(types, hints);
        AddMixedModeHint(module, hints);
        AddSectionHints(sections, hints);
        AddResourceHints(resources, hints);
        AddCoreReferenceHint(module, hints);
        AddEncryptedStringsHint(rawStrings, hints);
        AddReflectionLoaderHint(module, hints);
        AddEntryPointHint(module, hints);

        return hints
            .OrderByDescending(h => h.Severity)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddKnownProtectorHints(
        List<string> typeNames,
        List<string> assemblyAttributes,
        List<ProtectionHint> hints)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (marker, protector) in KnownProtectors)
        {
            bool inTypes;
            bool inAttributes;
            try
            {
                inTypes = typeNames.Any(t => t.Contains(marker, StringComparison.OrdinalIgnoreCase));
                inAttributes = assemblyAttributes.Any(a => a.Contains(marker, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            if ((!inTypes && !inAttributes) || !added.Add(protector))
                continue;

            hints.Add(new ProtectionHint
            {
                Name = protector,
                Description = "Well-known protector fingerprint found in metadata.",
                Severity = ProtectionSeverity.Suspicious,
                Evidence = inAttributes
                    ? $"{marker} (custom attribute / assembly attribute)"
                    : $"{marker} (type metadata)",
            });
        }
    }

    private static void AddSuppressIldasmHint(ModuleDefMD module, List<string> assemblyAttributes, List<ProtectionHint> hints)
    {
        bool moduleAttribute = false;
        try
        {
            moduleAttribute = module.CustomAttributes
                .Any(a => a.TypeFullName.Contains("SuppressIldasm", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // malformed module attribute table
        }

        if (moduleAttribute ||
            assemblyAttributes.Any(a => a.Contains("SuppressIldasm", StringComparison.OrdinalIgnoreCase)))
        {
            hints.Add(new ProtectionHint
            {
                Name = "SuppressIldasm",
                Description = "The assembly asks ILDasm to refuse disassembly.",
                Severity = ProtectionSeverity.Info,
                Evidence = "System.Runtime.CompilerServices.SuppressIldasmAttribute",
            });
        }
    }

    private static void AddNameManglingHint(IReadOnlyList<TypeEntry> types, List<ProtectionHint> hints)
    {
        if (types.Count == 0)
            return;

        int suspiciousNames = types.Count(t => t.IsObfuscated);
        if (suspiciousNames > Math.Max(8, types.Count / 20))
        {
            hints.Add(new ProtectionHint
            {
                Name = "Name mangling",
                Description = "A large share of type names are non-printable or machine generated.",
                Severity = ProtectionSeverity.Suspicious,
                Evidence = $"{suspiciousNames} of {types.Count} types",
            });
        }
    }

    private static void AddStrippedBodiesHint(IReadOnlyList<TypeEntry> types, List<ProtectionHint> hints)
    {
        int bodyless = 0;
        int methods = 0;
        foreach (TypeEntry type in types)
        {
            foreach (MethodEntry method in type.Methods)
            {
                // Abstract and runtime-implemented methods (P/Invoke, internal
                // call, COM stubs) legitimately have no body and would otherwise
                // be counted as "stripped".
                if (method.IsAbstract || method.IsPInvoke || method.IsRuntime)
                    continue;
                methods++;
                if (!method.HasBody)
                    bodyless++;
            }
        }

        if (methods > 0 && bodyless * 100L / methods > 20)
        {
            hints.Add(new ProtectionHint
            {
                Name = "Stripped method bodies",
                Description = "Methods without IL: typical for native stubs, JIT hooks or encrypted bodies.",
                Severity = ProtectionSeverity.Suspicious,
                Evidence = $"{bodyless} of {methods} non-abstract methods",
            });
        }
    }

    private static void AddMixedModeHint(ModuleDefMD module, List<ProtectionHint> hints)
    {
        bool ilOnly;
        try
        {
            ilOnly = module.IsILOnly;
        }
        catch
        {
            return;
        }

        if (ilOnly)
            return;

        // C++/CLI assemblies are legitimately mixed-mode; this is context, not a
        // verdict, so it must never outweigh real protector evidence.
        hints.Add(new ProtectionHint
        {
            Name = "Mixed-mode image",
            Description = "Contains native code next to managed metadata (C++/CLI, or a packer/protector stub).",
            Severity = ProtectionSeverity.Info,
            Evidence = "CLI header ILONLY flag is not set",
        });
    }

    private static void AddSectionHints(IReadOnlyList<PeSectionInfo> sections, List<ProtectionHint> hints)
    {
        foreach (PeSectionInfo section in sections)
        {
            if (section.Entropy >= 7.5 && section.RawSize > 0x2000)
            {
                // High entropy in a non-executable data section is normal for
                // compressed resources; executable/writable high-entropy data is
                // the interesting case.
                bool codeLike = section.IsExecutable || section.IsWritable;
                hints.Add(new ProtectionHint
                {
                    Name = "High entropy section",
                    Description = "Encrypted or compressed content inside " + section.Name + ".",
                    Severity = codeLike ? ProtectionSeverity.Suspicious : ProtectionSeverity.Info,
                    Evidence = $"entropy {section.Entropy.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"
                               + $" / 8.00 in {section.Name} ({section.FlagsLabel})",
                });
            }

            if (section.IsWritable && section.IsExecutable)
            {
                hints.Add(new ProtectionHint
                {
                    Name = "Writable + executable section",
                    Description = "RWX section " + section.Name + " can host unpacking stubs or injected code.",
                    Severity = ProtectionSeverity.Suspicious,
                    Evidence = $"{section.Name} flags {section.FlagsLabel}",
                });
            }
        }
    }

    private static void AddResourceHints(IReadOnlyList<ResourceEntry> resources, List<ProtectionHint> hints)
    {
        foreach (ResourceEntry resource in resources.Where(r => r.Size > 256 * 1024))
        {
            hints.Add(new ProtectionHint
            {
                Name = "Large embedded resource",
                Description = "Embedded payloads of this size are often packed assemblies or native binaries.",
                Severity = ProtectionSeverity.Info,
                Evidence = $"{resource.Name} · {resource.SizeLabel}",
            });
        }
    }

    private static void AddCoreReferenceHint(ModuleDefMD module, List<ProtectionHint> hints)
    {
        try
        {
            if (module.Assembly == null || module.Kind == ModuleKind.NetModule)
                return;
        }
        catch
        {
            return;
        }

        bool hasCoreReference;
        try
        {
            hasCoreReference = module.GetAssemblyRefs().Any(r =>
                r.Name == "mscorlib" || r.Name == "System.Runtime" || r.Name == "netstandard");
        }
        catch
        {
            return;
        }

        if (hasCoreReference)
            return;

        hints.Add(new ProtectionHint
        {
            Name = "Missing core library reference",
            Description = "No mscorlib/System.Runtime reference — metadata was probably rewritten.",
            Severity = ProtectionSeverity.Info,
            Evidence = "assembly reference table",
        });
    }

    private static void AddEncryptedStringsHint(
        Dictionary<string, (int Count, string Kind)> rawStrings,
        List<ProtectionHint> hints)
    {
        int encryptedStrings;
        try
        {
            encryptedStrings = rawStrings.Count(s => s.Value.Kind == "user" && LooksEncrypted(s.Key));
        }
        catch
        {
            return;
        }

        if (encryptedStrings <= 20)
            return;

        hints.Add(new ProtectionHint
        {
            Name = "Encrypted string table",
            Description = "Many user strings look like base64/ciphertext blobs rather than text.",
            Severity = ProtectionSeverity.Info,
            Evidence = $"{encryptedStrings} suspicious literals",
        });
    }

    private static void AddReflectionLoaderHint(ModuleDefMD module, List<ProtectionHint> hints)
    {
        try
        {
            var memberRefs = module.GetMemberRefs().ToList();
            bool loadsAssembly = memberRefs.Any(r =>
                IsMemberOf(r, "System.Reflection.Assembly", "Load", "LoadFrom", "LoadFile") ||
                IsMemberOf(r, "System.Reflection.AssemblyName", "GetAssemblyName") ||
                IsMemberOf(r, "System.Reflection.Module", "Load"));
            bool reflects = memberRefs.Any(r =>
                IsMemberOf(r, "System.Reflection.Assembly", "GetTypes", "GetType", "GetManifestResourceStream") ||
                IsMemberOf(r, "System.Reflection.Module", "GetTypes", "GetType") ||
                IsMemberOf(r, "System.Activator", "CreateInstance"));

            if (loadsAssembly && reflects)
            {
                hints.Add(new ProtectionHint
                {
                    Name = "Reflection driven loader",
                    Description = "Metadata references runtime loading and reflection APIs used by unpackers.",
                    Severity = ProtectionSeverity.Info,
                    Evidence = "Assembly.Load*/GetTypes/Activator.CreateInstance members",
                });
            }
        }
        catch
        {
            // malformed member reference table
        }
    }

    private static bool IsMemberOf(MemberRef member, string declaringType, params string[] names)
    {
        try
        {
            if (member.Class == null || !member.Class.FullName.Equals(declaringType, StringComparison.Ordinal))
                return false;
            string? name = member.Name?.String;
            return name != null && names.Contains(name, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void AddEntryPointHint(ModuleDefMD module, List<ProtectionHint> hints)
    {
        try
        {
            ModuleKind kind = module.Kind;
            if (kind is not (ModuleKind.Console or ModuleKind.Windows))
                return;
            if (module.EntryPoint == null)
            {
                hints.Add(new ProtectionHint
                {
                    Name = "No managed entry point",
                    Description = "The module is executable but exposes no managed entry point.",
                    Severity = ProtectionSeverity.Info,
                    Evidence = "CLI header",
                });
            }
        }
        catch
        {
            // unreadable module kind/entry point
        }
    }

    private static List<string> SafeAttributeNames(ModuleDefMD module)
    {
        var names = new List<string>();
        try
        {
            if (module.Assembly == null)
                return names;
            foreach (CustomAttribute attribute in module.Assembly.CustomAttributes)
            {
                names.Add(attribute.TypeFullName);
                foreach (var argument in attribute.ConstructorArguments)
                {
                    string? text = argument.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        names.Add(text!);
                }
            }
        }
        catch
        {
            // Attribute blobs can be malformed in protected samples.
        }
        return names;
    }

    private static bool LooksEncrypted(string value)
    {
        if (value.Length < 24)
            return false;

        // Prose is never ciphertext: any whitespace disqualifies a literal. This
        // removed the old false positive where ordinary long strings were flagged
        // because spaces were not counted as symbols.
        bool hasWhitespace = false;
        int letters = 0;
        int digits = 0;
        int symbols = 0;
        int base64Chars = 0;
        bool hasUpper = false;
        bool hasLower = false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                hasWhitespace = true;
                continue;
            }

            if (char.IsLetter(c))
            {
                letters++;
                if (char.IsUpper(c)) hasUpper = true;
                else hasLower = true;
            }
            else if (char.IsDigit(c))
            {
                digits++;
            }
            else
            {
                symbols++;
            }

            bool isBase64Char = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '+' or '/' or '=';
            if (isBase64Char)
                base64Chars++;
        }

        if (hasWhitespace)
            return false;

        if (base64Chars == value.Length && hasUpper && hasLower && value.Length % 4 == 0)
            return true;

        // Symbol-heavy binary-looking blobs (e.g. XOR/RC4 output rendered as text).
        if (symbols > 0 && letters + digits < value.Length / 2)
            return true;

        return false;
    }
}
