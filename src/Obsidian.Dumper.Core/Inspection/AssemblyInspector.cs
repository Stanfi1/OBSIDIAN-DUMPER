using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Obsidian.Dumper.Shared;
using Machine = dnlib.PE.Machine;

namespace Obsidian.Dumper.Core.Inspection;

/// <summary>
/// Static analysis of a managed module: identity, metadata, IL inventory,
/// strings, references, resources, section entropy and protector fingerprints.
/// Works on dumped files as well as on arbitrary assemblies opened from disk.
/// </summary>
public sealed class AssemblyInspector : IDisposable
{
    /// <summary>Refuse to load files this large: the inspection buffers the whole image.</summary>
    private const long MaxInspectedFileBytes = int.MaxValue;

    private readonly ModuleDefMD _module;
    private readonly byte[] _fileBytes;
    private readonly Dictionary<int, TypeDef> _typesByToken = new();
    private readonly Dictionary<int, MethodDef> _methodsByToken = new();
    private List<TypeEntry> _types = new();
    private List<StringEntry> _strings = new();
    private AssemblySummary _summary = new();

    private AssemblyInspector(ModuleDefMD module, byte[] fileBytes, string? sourcePath)
    {
        _module = module;
        _fileBytes = fileBytes;
        SourcePath = sourcePath;
    }

    public string? SourcePath { get; }

    public AssemblySummary Summary => _summary;
    public IReadOnlyList<TypeEntry> Types => _types;
    public IReadOnlyList<StringEntry> Strings => _strings;

    /// <summary>Opens a managed module from disk. Throws a readable exception when the file is not readable CLI metadata.</summary>
    public static AssemblyInspector Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The assembly file does not exist.", path);

        byte[] bytes = ReadFileBytesShared(path);
        ModuleDefMD? module = null;
        try
        {
            module = ModuleDefMD.Load(bytes, new ModuleContext());
            var inspector = new AssemblyInspector(module, bytes, path);
            inspector.Build();
            return inspector;
        }
        catch (BadImageFormatException)
        {
            module?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            module?.Dispose();
            throw new BadImageFormatException(
                "The file does not contain readable .NET metadata (" + ex.Message + ").", path, ex);
        }
    }

    /// <summary>
    /// Opens an on-disk image held in memory. <paramref name="sourcePath"/> is used
    /// for decompilation and text output; pass the on-disk file bytes (a loader
    /// mapped image is accepted for identity data but not for decompilation).
    /// </summary>
    public static AssemblyInspector Open(byte[] bytes, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new BadImageFormatException("The image is empty.");

        ModuleDefMD? module = null;
        try
        {
            module = ModuleDefMD.Load(bytes, new ModuleContext());
            var inspector = new AssemblyInspector(module, bytes, sourcePath);
            inspector.Build();
            return inspector;
        }
        catch (BadImageFormatException)
        {
            module?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            module?.Dispose();
            throw new BadImageFormatException(
                "The image does not contain readable .NET metadata (" + ex.Message + ").", ex);
        }
    }

    /// <summary>
    /// Non-throwing open used by callers that must keep going (CLI, batch scans).
    /// Returns null and a human readable reason when the file cannot be inspected.
    /// </summary>
    public static bool TryOpen(string path, out AssemblyInspector? inspector, out string? error)
    {
        inspector = null;
        error = null;
        try
        {
            inspector = Open(path);
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeLoadFailure(path, ex);
            return false;
        }
    }

    /// <summary>Quick check that a file contains readable CLI metadata.</summary>
    public static bool IsManagedFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (new FileInfo(path).Length is <= 0 or > MaxInspectedFileBytes)
                return false;

            byte[] bytes = ReadFileBytesShared(path);
            if (!PeImageLayout.TryGetMetadataRootFromFile(bytes, out _, out _))
                return false;
            using var module = ModuleDefMD.Load(bytes, new ModuleContext());
            return module.Types.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeLoadFailure(string path, Exception exception)
    {
        if (exception is FileNotFoundException notFound)
            return notFound.Message;
        if (exception is IOException or UnauthorizedAccessException)
            return $"The file could not be read: {exception.Message}";

        string size = "?";
        try
        {
            size = new FileInfo(path).Length.ToString();
        }
        catch
        {
            // keep "?"
        }

        return $"Not a readable .NET assembly ({exception.GetType().Name}: {exception.Message}; file={size} bytes).";
    }

    /// <summary>
    /// Reads the whole file with sharing flags, so a module that is still loaded
    /// or being written by another process can be inspected instead of throwing
    /// "file is in use".
    /// </summary>
    private static byte[] ReadFileBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length <= 0)
            return Array.Empty<byte>();
        if (length > MaxInspectedFileBytes)
            throw new IOException($"The file is too large to inspect ({length:N0} bytes).");

        var bytes = new byte[length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read <= 0)
                break;
            offset += read;
        }
        if (offset != bytes.Length)
            Array.Resize(ref bytes, offset);
        return bytes;
    }

    private void Build()
    {
        var diagnostics = new List<string>();
        var types = new List<TypeEntry>(Math.Max(16, SafeTypeCount() * 2));
        var strings = new Dictionary<string, (int Count, string Kind)>(StringComparer.Ordinal);
        int methodTotal = 0;
        int fieldTotal = 0;
        int propertyTotal = 0;
        int eventTotal = 0;
        int ilTotal = 0;
        int namespaces = 0;
        var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);

        List<TypeDef> typeDefs = new(types.Capacity);
        try
        {
            typeDefs.AddRange(_module.GetTypes());
        }
        catch (Exception ex)
        {
            diagnostics.Add("type table unreadable: " + ex.Message);
        }

        foreach (TypeDef type in typeDefs)
        {
            try
            {
                if (type.FullName == "<Module>")
                    continue;

                SetTokenMaps(type);

                var methods = new List<MethodEntry>(type.Methods.Count);
                int typeIl = 0;
                foreach (MethodDef method in type.Methods)
                {
                    MethodEntry entry;
                    try
                    {
                        entry = DescribeMethod(method);
                    }
                    catch (Exception ex)
                    {
                        entry = new MethodEntry
                        {
                            Name = SafeName(method),
                            Signature = SafeName(method),
                            Token = SafeToken(method),
                            HasBody = false,
                            Access = "private",
                        };
                        diagnostics.Add($"{type.FullName}::{SafeName(method)}: {ex.Message}");
                    }

                    methods.Add(entry);
                    if (entry.HasBody)
                    {
                        typeIl += entry.IlSize;
                        CollectStrings(method, strings);
                    }
                }

                string ns = EffectiveNamespace(type);
                if (ns.Length > 0 && namespacesSeen.Add(ns))
                    namespaces++;

                types.Add(new TypeEntry
                {
                    Name = type.Name?.String ?? "?",
                    Namespace = ns,
                    FullName = type.FullName ?? "?",
                    BaseType = SafeBaseType(type),
                    Kind = ResolveKind(type),
                    Visibility = DescribeVisibility(type),
                    MethodCount = type.Methods.Count,
                    FieldCount = type.Fields.Count,
                    PropertyCount = type.Properties.Count,
                    NestedLevel = CountNesting(type),
                    Token = (int)type.MDToken.Raw,
                    IsCompilerGenerated = type.CustomAttributes.IsDefined(
                        "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
                    IsObfuscated = LooksObfuscated(type.Name?.String ?? string.Empty),
                    TotalMethodCount = CountMethodsRecursive(type),
                    IlSize = typeIl,
                    Methods = methods,
                });

                methodTotal += type.Methods.Count;
                fieldTotal += type.Fields.Count;
                propertyTotal += type.Properties.Count;
                eventTotal += type.Events.Count;
                ilTotal += typeIl;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"type metadata: {ex.Message}");
            }
        }

        _types = types.OrderBy(t => t.Namespace, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();

        // Metadata names are part of the string inventory: collect them before
        // the dictionary is materialised (previously they were collected after
        // the snapshot was taken and silently never surfaced).
        CollectMetadataStrings(strings);

        _strings = strings
            .Select(kv => new StringEntry { Value = kv.Key, Kind = kv.Value.Kind, Count = kv.Value.Count })
            .OrderByDescending(s => s.Length)
            .ThenBy(s => s.Value, StringComparer.Ordinal)
            .Take(20000)
            .ToList();

        var references = new List<ReferenceEntry>();
        try
        {
            foreach (AssemblyRef reference in _module.GetAssemblyRefs())
            {
                references.Add(new ReferenceEntry
                {
                    Name = reference.Name?.String ?? "?",
                    Version = reference.Version?.ToString() ?? string.Empty,
                    Culture = reference.Culture?.String ?? string.Empty,
                    PublicKeyToken = FormatToken(reference.PublicKeyOrToken),
                    Kind = "assembly",
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add("assembly references: " + ex.Message);
        }

        try
        {
            foreach (ModuleRef reference in _module.GetModuleRefs())
            {
                references.Add(new ReferenceEntry
                {
                    Name = reference.Name?.String ?? "?",
                    Kind = "module",
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add("module references: " + ex.Message);
        }

        var resources = new List<ResourceEntry>();
        try
        {
            foreach (Resource resource in _module.Resources)
            {
                long size = 0;
                if (resource is EmbeddedResource embedded)
                {
                    try
                    {
                        size = embedded.Length;
                    }
                    catch
                    {
                        size = 0;
                    }
                }

                resources.Add(new ResourceEntry
                {
                    Name = resource.Name?.String ?? "?",
                    Size = size,
                    Kind = DescribeResourceKind(resource),
                    IsPublic = resource.IsPublic,
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add("resource table: " + ex.Message);
        }

        var sections = BuildSections(diagnostics);
        IReadOnlyList<ProtectionHint> protections;
        try
        {
            protections = ProtectionAnalyzer.Analyze(_module, _types, sections, resources, strings);
        }
        catch (Exception ex)
        {
            diagnostics.Add("protection analysis: " + ex.Message);
            protections = Array.Empty<ProtectionHint>();
        }

        _summary = new AssemblySummary
        {
            Name = SafeAssemblyString(a => a.Name?.String) ?? SafeModuleString(m => m.Name?.String) ?? "?",
            FullName = SafeAssemblyString(a => a.FullName) ?? SafeModuleString(m => m.Name?.String) ?? "?",
            Version = SafeAssemblyString(a => a.Version?.ToString()) ?? string.Empty,
            Culture = SafeAssemblyString(a => a.Culture?.String) ?? string.Empty,
            PublicKeyToken = FormatToken(SafeAssembly(a => a.PublicKeyToken)),
            ModuleName = SafeModuleString(m => m.Name?.String) ?? "?",
            RuntimeVersion = SafeMetadataString() ?? "?",
            Kind = ResolveKind(_module),
            EntryPoint = SafeEntryPoint(),
            TargetFramework = ReadTargetFramework(),
            IsStrongNamed = SafeBool(() => _module.Assembly?.HasPublicKey == true),
            IsIlOnly = SafeBool(() => _module.IsILOnly),
            IsMixedMode = !SafeBool(() => _module.IsILOnly),
            IsReadyToRun = ContainsReadyToRun(),
            Machine = SafeMachine(),
            Subsystem = SafeSubsystem(),
            CompileTimestamp = TryGetTimestamp(),
            Mvid = SafeMvid(),
            Company = ReadAssemblyAttribute("AssemblyCompanyAttribute"),
            Product = ReadAssemblyAttribute("AssemblyProductAttribute"),
            Description = ReadAssemblyAttribute("AssemblyDescriptionAttribute"),
            Copyright = ReadAssemblyAttribute("AssemblyCopyrightAttribute"),
            FileVersion = ReadAssemblyAttribute("AssemblyFileVersionAttribute"),
            TypeCount = _types.Count,
            MethodCount = methodTotal,
            FieldCount = fieldTotal,
            PropertyCount = propertyTotal,
            EventCount = eventTotal,
            NamespaceCount = namespaces,
            ResourceCount = resources.Count,
            AssemblyRefCount = references.Count(r => r.Kind == "assembly"),
            TypeRefCount = SafeCount(() => _module.GetTypeRefs().Count()),
            MemberRefCount = SafeCount(() => _module.GetMemberRefs().Count()),
            CustomAttributeCount = CountCustomAttributes(),
            IlBytes = ilTotal,
            FileSize = _fileBytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(_fileBytes)).ToLowerInvariant(),
            SourcePath = SourcePath,
            Diagnostics = diagnostics.Count == 0 ? null : string.Join(" · ", diagnostics),
            Sections = sections,
            Protections = protections,
            References = references,
            Resources = resources,
        };
    }

    private void SetTokenMaps(TypeDef type)
    {
        _typesByToken[(int)type.MDToken.Raw] = type;
        foreach (MethodDef method in type.Methods)
            _methodsByToken[(int)method.MDToken.Raw] = method;
    }

    private void CollectMetadataStrings(Dictionary<string, (int Count, string Kind)> strings)
    {
        AddString(strings, SafeAssemblyString(a => a.Name?.String), "metadata");
        AddString(strings, SafeAssemblyString(a => a.FullName), "metadata");
        AddString(strings, SafeModuleString(m => m.Name?.String), "metadata");
        AddString(strings, ReadTargetFramework(), "metadata");

        foreach (TypeDef type in _typesByToken.Values)
        {
            try
            {
                AddString(strings, type.Name?.String, "metadata");
                AddString(strings, type.Namespace?.String, "metadata");
                foreach (GenericParam genericParameter in type.GenericParameters)
                    AddString(strings, genericParameter.Name?.String, "metadata");

                foreach (MethodDef method in type.Methods)
                {
                    AddString(strings, method.Name?.String, "metadata");
                    foreach (GenericParam genericParameter in method.GenericParameters)
                        AddString(strings, genericParameter.Name?.String, "metadata");
                    foreach (ParamDef parameter in method.ParamDefs)
                        AddString(strings, parameter.Name?.String, "metadata");
                }

                foreach (FieldDef field in type.Fields)
                    AddString(strings, field.Name?.String, "metadata");
                foreach (PropertyDef property in type.Properties)
                    AddString(strings, property.Name?.String, "metadata");
                foreach (EventDef @event in type.Events)
                    AddString(strings, @event.Name?.String, "metadata");
            }
            catch
            {
                // A malformed row must not abort the whole string inventory.
            }
        }

        try
        {
            foreach (AssemblyRef reference in _module.GetAssemblyRefs())
                AddString(strings, reference.Name?.String, "reference");
            foreach (Resource resource in _module.Resources)
                AddString(strings, resource.Name?.String, "resource");
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void AddString(Dictionary<string, (int Count, string Kind)> strings, string? value, string kind)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
            return;
        strings.TryGetValue(value, out var existing);
        strings[value] = (existing.Count + 1, existing.Kind == "user" ? "user" : kind);
    }

    private void CollectStrings(MethodDef method, Dictionary<string, (int Count, string Kind)> strings)
    {
        try
        {
            if (!method.HasBody)
                return;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code != Code.Ldstr)
                    continue;
                if (instruction.Operand is string text && !string.IsNullOrEmpty(text))
                    AddString(strings, text, "user");
            }
        }
        catch
        {
            // Malformed IL is common in packed samples; the rest of the dump is still useful.
        }
    }

    private MethodEntry DescribeMethod(MethodDef method)
    {
        bool hasBody = method.HasBody;
        int ilSize = 0;
        int maxStack = 0;
        int locals = 0;
        if (hasBody)
        {
            try
            {
                ilSize = method.Body.Instructions.Sum(i => i.GetSize());
                maxStack = method.Body.MaxStack;
                locals = method.Body.Variables.Count;
            }
            catch
            {
                hasBody = false;
            }
        }

        var parameters = new List<string>(method.MethodSig?.Params.Count ?? 0);
        if (method.MethodSig != null)
        {
            foreach (TypeSig parameter in method.MethodSig.Params)
                parameters.Add(ShortType(parameter));
        }

        string returnType = ShortType(method.MethodSig?.RetType);
        string name = SafeName(method);
        string signature = $"{returnType} {name}({string.Join(", ", parameters)})";

        bool isRuntime;
        try
        {
            isRuntime = (method.ImplAttributes & MethodImplAttributes.Runtime) != 0;
        }
        catch
        {
            isRuntime = false;
        }

        return new MethodEntry
        {
            Name = name,
            Signature = signature,
            ReturnType = returnType,
            Token = SafeToken(method),
            Rva = SafeRva(method),
            IlSize = ilSize,
            MaxStack = maxStack,
            LocalCount = locals,
            IsStatic = SafeBool(() => method.IsStatic),
            IsVirtual = SafeBool(() => method.IsVirtual),
            IsAbstract = SafeBool(() => method.IsAbstract),
            IsConstructor = SafeBool(() => method.IsConstructor),
            IsPInvoke = SafeBool(() => method.IsPinvokeImpl),
            IsRuntime = isRuntime,
            HasBody = hasBody,
            Access = DescribeAccess(method),
            Flags = DescribeMethodFlags(method),
        };
    }

    private static string DescribeAccess(MethodDef method)
    {
        try
        {
            if (method.IsPublic) return "public";
            if (method.IsPrivate) return "private";
            if (method.IsFamily) return "protected";
            if (method.IsAssembly) return "internal";
            if (method.IsFamilyOrAssembly) return "protected internal";
            if (method.IsFamilyAndAssembly) return "private protected";
        }
        catch
        {
            // fall through to the default
        }
        return "private";
    }

    private static string DescribeMethodFlags(MethodDef method)
    {
        var flags = new List<string>(4);
        try
        {
            if (method.IsStatic) flags.Add("static");
            if (method.IsAbstract) flags.Add("abstract");
            else if (method.IsVirtual) flags.Add("virtual");
            if (method.IsPinvokeImpl) flags.Add("pinvoke");
            if (method.IsConstructor) flags.Add("ctor");
            if (method.IsSpecialName) flags.Add("special");
        }
        catch
        {
            // best effort
        }
        return string.Join(" ", flags);
    }

    private static string ShortType(TypeSig? sig)
    {
        if (sig == null)
            return "void";
        try
        {
            string name = sig.TypeName;
            return string.IsNullOrEmpty(name) ? "?" : name.Replace("/", "+");
        }
        catch
        {
            return "?";
        }
    }

    private static TypeKind ResolveKind(TypeDef type)
    {
        try
        {
            if (type.IsInterface)
                return TypeKind.Interface;
            if (type.IsEnum)
                return TypeKind.Enum;
            string? baseName = type.BaseType?.FullName;
            if (baseName is "System.MulticastDelegate" or "System.Delegate")
                return TypeKind.Delegate;
            if (type.IsValueType)
                return TypeKind.Struct;
        }
        catch
        {
            // malformed type flags: fall back to class
        }
        return TypeKind.Class;
    }

    private static string DescribeVisibility(TypeDef type)
    {
        try
        {
            if (type.IsPublic) return "public";
            if (type.IsNestedPublic) return "public";
            if (type.IsNestedPrivate) return "private";
            if (type.IsNestedFamily) return "protected";
            if (type.IsNestedAssembly) return "internal";
            if (type.IsNestedFamilyOrAssembly) return "protected internal";
            if (type.IsNestedFamilyAndAssembly) return "private protected";
        }
        catch
        {
            // fall through
        }
        return "internal";
    }

    private static int CountNesting(TypeDef type)
    {
        int depth = 0;
        try
        {
            ITypeDefOrRef? current = type.DeclaringType;
            while (current != null && depth < 128)
            {
                depth++;
                current = (current as TypeDef)?.DeclaringType;
            }
        }
        catch
        {
            // malformed declaring-type chain
        }
        return depth;
    }

    /// <summary>Nested types inherit the namespace of their outermost declaring type.</summary>
    private static string EffectiveNamespace(TypeDef type)
    {
        try
        {
            TypeDef current = type;
            int guard = 0;
            while (current.DeclaringType is TypeDef parent && guard++ < 128)
                current = parent;
            return current.Namespace?.String ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Methods of the type plus every nested type, recursively.</summary>
    private static int CountMethodsRecursive(TypeDef type)
    {
        int total = 0;
        var pending = new Stack<TypeDef>();
        var seen = new HashSet<TypeDef>();
        pending.Push(type);
        while (pending.Count > 0)
        {
            TypeDef current = pending.Pop();
            if (!seen.Add(current))
                continue;
            try
            {
                total += current.Methods.Count;
                foreach (TypeDef nested in current.NestedTypes)
                    pending.Push(nested);
            }
            catch
            {
                // stop descending into a malformed nested type
            }
        }
        return total;
    }

    private static bool LooksObfuscated(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        foreach (char c in name)
        {
            if (c < 32 || c > 0x7E)
                return true;
            if (c is '\u00A0' or '\u200B' or '\u202E')
                return true;
        }
        return name.Contains("\\u", StringComparison.Ordinal) || name.Length > 200;
    }

    private static string ResolveKind(ModuleDefMD module)
    {
        try
        {
            return module.Kind switch
            {
                ModuleKind.Windows => "EXE",
                ModuleKind.Console => "EXE",
                _ => "DLL",
            };
        }
        catch
        {
            return "DLL";
        }
    }

    private string SafeMachine()
    {
        try
        {
            return DescribeMachine(_module.Machine);
        }
        catch
        {
            return "?";
        }
    }

    private string SafeSubsystem()
    {
        try
        {
            return _module.Kind.ToString().ToLowerInvariant();
        }
        catch
        {
            return "?";
        }
    }

    private static string DescribeMachine(Machine machine) => machine switch
    {
        Machine.I386 => "x86",
        Machine.AMD64 => "x64",
        Machine.ARM64 => "ARM64",
        Machine.ARM => "ARM",
        Machine.IA64 => "IA64",
        Machine.ARMNT => "ARM",
        _ => machine.ToString(),
    };

    private DateTime? TryGetTimestamp()
    {
        try
        {
            uint stamp = _module.Metadata.PEImage?.ImageNTHeaders?.FileHeader.TimeDateStamp ?? 0;
            if (stamp == 0)
                return null;
            DateTime value = DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime;

            // Deterministic builds store a content hash in TimeDateStamp.
            if (value.Year < 2000 || value > DateTime.Now.AddYears(1))
                return null;
            return value;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadTargetFramework()
    {
        try
        {
            CustomAttribute? attribute = _module.Assembly?.CustomAttributes
                .FirstOrDefault(a => a.TypeFullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
            if (attribute?.ConstructorArguments.Count > 0)
                return attribute.ConstructorArguments[0].Value?.ToString();
        }
        catch
        {
            // Attribute blobs of packed assemblies are frequently unreadable.
        }
        return null;
    }

    private string? ReadAssemblyAttribute(string attributeName)
    {
        try
        {
            CustomAttribute? attribute = _module.Assembly?.CustomAttributes
                .FirstOrDefault(a => a.TypeFullName == "System.Reflection." + attributeName);
            if (attribute?.ConstructorArguments.Count > 0)
                return attribute.ConstructorArguments[0].Value?.ToString();
        }
        catch
        {
            // ignore malformed attribute blobs
        }
        return null;
    }

    private static string FormatToken(PublicKeyBase? token)
    {
        if (token == null || token.IsNullOrEmpty)
            return string.Empty;
        try
        {
            byte[] bytes = token.Data;
            return bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// ReadyToRun images carry an "RTR0" native header; its RVA is stored in the
    /// CLI header's ManagedNativeHeader directory. Scanning the whole file for
    /// the magic would also match random string data, so resolve the header.
    /// </summary>
    private bool ContainsReadyToRun()
    {
        // On-disk images are the normal input; a loader-mapped image is also
        // accepted and uses the mapped RVA translation.
        return ContainsReadyToRun(mapped: false) || ContainsReadyToRun(mapped: true);
    }

    private bool ContainsReadyToRun(bool mapped)
    {
        try
        {
            if (!PeImageLayout.TryGetCliHeader(_fileBytes, mapped, out CliHeader cli))
                return false;
            if (!PeImageLayout.TryReadHeaders(_fileBytes, out PeHeaders headers))
                return false;

            const int managedNativeHeaderOffset = 64; // IMAGE_COR20_HEADER.ManagedNativeHeader
            int headerEnd = cli.Offset + managedNativeHeaderOffset + 8;
            if (cli.Offset < 0 || headerEnd > _fileBytes.Length)
                return false;

            uint nativeHeaderRva = PeImageLayout.ReadU32(_fileBytes, cli.Offset + managedNativeHeaderOffset);
            if (nativeHeaderRva == 0)
                return false;

            int offset = mapped
                ? PeImageLayout.MapRva(headers, nativeHeaderRva)
                : PeImageLayout.RvaToFileOffset(headers, nativeHeaderRva);
            if (offset < 0 || offset + 4 > _fileBytes.Length)
                return false;
            return PeImageLayout.ReadU32(_fileBytes, offset) == 0x30525452; // "RTR0"
        }
        catch
        {
            return false;
        }
    }

    private List<PeSectionInfo> BuildSections(List<string> diagnostics)
    {
        var sections = new List<PeSectionInfo>();
        try
        {
            if (!PeImageLayout.TryReadHeaders(_fileBytes, out PeHeaders headers))
                return sections;

            foreach (PeSection section in headers.Sections)
            {
                uint rawSize = (uint)Math.Max(0L, Math.Min(section.RawSize, (long)_fileBytes.Length - section.RawPointer));
                double entropy = 0;
                if (rawSize > 0)
                {
                    // Entropy is computed over the existing buffer: copying every
                    // section first doubled peak memory for big assemblies.
                    entropy = Entropy(_fileBytes.AsSpan((int)section.RawPointer, (int)rawSize));
                }

                sections.Add(new PeSectionInfo
                {
                    Name = section.Name,
                    VirtualAddress = section.VirtualAddress,
                    VirtualSize = section.VirtualSize,
                    RawSize = section.RawSize,
                    Characteristics = section.Characteristics,
                    Entropy = entropy,
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add("section table: " + ex.Message);
        }
        return sections;
    }

    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return 0;
        Span<int> counts = stackalloc int[256];
        foreach (byte b in data)
            counts[b]++;

        double entropy = 0;
        double length = data.Length;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0)
                continue;
            double p = counts[i] / length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    public IReadOnlyList<MethodEntry> MethodsOf(TypeEntry type)
        => type.Methods;

    public string DecompileType(TypeEntry type)
        => DecompilerService.Shared.DecompileType(_module, SourcePath, type.FullName, type.Name);

    public string Disassemble(MethodEntry method)
    {
        try
        {
            return _methodsByToken.TryGetValue(method.Token, out MethodDef? methodDef)
                ? IlFormatter.Format(_module, methodDef)
                : "// method not found";
        }
        catch (Exception ex)
        {
            return "// IL unavailable: " + ex.Message;
        }
    }

    public string DisassembleType(TypeEntry type)
    {
        try
        {
            if (!_typesByToken.TryGetValue(type.Token, out TypeDef? declaring))
                return "// type not found";

            var builder = new StringBuilder(16 * 1024);
            foreach (MethodDef method in declaring.Methods)
            {
                if (!method.HasBody)
                    continue;
                try
                {
                    builder.AppendLine(IlFormatter.Format(_module, method));
                    builder.AppendLine();
                }
                catch (Exception ex)
                {
                    builder.AppendLine("// " + SafeName(method) + ": IL unavailable: " + ex.Message);
                }
            }
            return builder.Length == 0 ? "// no method bodies" : builder.ToString();
        }
        catch (Exception ex)
        {
            return "// IL unavailable: " + ex.Message;
        }
    }

    public void Dispose() => _module.Dispose();

    // ---------------------------------------------------------------- safe helpers

    private int SafeTypeCount()
    {
        try
        {
            return _module.Types.Count;
        }
        catch
        {
            return 16;
        }
    }

    private static int SafeCount(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private string? SafeMetadataString()
    {
        try
        {
            return _module.Metadata?.VersionString;
        }
        catch
        {
            return null;
        }
    }

    private string? SafeMvid()
    {
        try
        {
            return _module.Mvid?.ToString("D");
        }
        catch
        {
            return null;
        }
    }

    private string? SafeEntryPoint()
    {
        try
        {
            MethodDef? entryPoint = _module.EntryPoint;
            return entryPoint != null
                ? $"{entryPoint.DeclaringType?.FullName}::{entryPoint.Name}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private T? SafeAssembly<T>(Func<AssemblyDef, T?> read) where T : class
    {
        try
        {
            AssemblyDef? assembly = _module.Assembly;
            return assembly == null ? null : read(assembly);
        }
        catch
        {
            return null;
        }
    }

    private string? SafeAssemblyString(Func<AssemblyDef, string?> read)
    {
        try
        {
            AssemblyDef? assembly = _module.Assembly;
            return assembly == null ? null : read(assembly);
        }
        catch
        {
            return null;
        }
    }

    private string? SafeModuleString(Func<ModuleDef, string?> read)
    {
        try
        {
            return read(_module);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeName(MethodDef method)
    {
        try
        {
            return method.Name?.String ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string? SafeBaseType(TypeDef type)
    {
        try
        {
            return type.BaseType?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static int SafeToken(MethodDef method)
    {
        try
        {
            return (int)method.MDToken.Raw;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeRva(MethodDef method)
    {
        try
        {
            return (int)method.RVA;
        }
        catch
        {
            return 0;
        }
    }

    private int CountCustomAttributes()
    {
        int total = 0;
        try
        {
            if (_module.Assembly != null)
                total += _module.Assembly.CustomAttributes.Count;
            total += _module.CustomAttributes.Count;

            foreach (TypeDef type in _typesByToken.Values)
            {
                try
                {
                    total += type.CustomAttributes.Count;
                    foreach (GenericParam gp in type.GenericParameters)
                        total += gp.CustomAttributes.Count;
                    foreach (FieldDef field in type.Fields)
                        total += field.CustomAttributes.Count;
                    foreach (PropertyDef property in type.Properties)
                        total += property.CustomAttributes.Count;
                    foreach (EventDef @event in type.Events)
                        total += @event.CustomAttributes.Count;
                    foreach (MethodDef method in type.Methods)
                    {
                        total += method.CustomAttributes.Count;
                        foreach (ParamDef parameter in method.ParamDefs)
                            total += parameter.CustomAttributes.Count;
                        foreach (GenericParam gp in method.GenericParameters)
                            total += gp.CustomAttributes.Count;
                    }
                }
                catch
                {
                    // skip malformed rows
                }
            }
        }
        catch
        {
            // Malformed attribute tables are common in protected assemblies.
        }
        return total;
    }

    private static string DescribeResourceKind(Resource resource)
    {
        try
        {
            return resource.ResourceType switch
            {
                ResourceType.Embedded => "embedded",
                ResourceType.AssemblyLinked => "assembly-linked",
                ResourceType.Linked => "linked",
                _ => "unknown",
            };
        }
        catch
        {
            return "unknown";
        }
    }
}
