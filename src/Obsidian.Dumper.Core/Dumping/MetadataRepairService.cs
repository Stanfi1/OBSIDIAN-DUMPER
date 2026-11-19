using Microsoft.Diagnostics.Runtime;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core.Dumping;

public sealed class MetadataRepairResult
{
    public bool Success { get; init; }
    public bool Changed { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? OutputPath { get; init; }
}

/// <summary>
/// Repairs the .NET metadata directory of a dumped image using the metadata the
/// CLR itself exposes through ClrMD. Many anti-dump protections leave the PE
/// image valid but zero the COR20 metadata directory or the BSJB signature; the
/// runtime still knows the real metadata address and length, so the dumped file
/// can be patched back into a loadable assembly without injecting code.
/// </summary>
public static class MetadataRepairService
{
    private const uint MetadataSignature = 0x424A5342; // "BSJB"
    private const int Cor20HeaderSize = 72;

    public static MetadataRepairResult TryRepair(
        int processId,
        ulong imageBase,
        string filePath,
        string? moduleName = null)
    {
        if (!File.Exists(filePath))
        {
            return new MetadataRepairResult
            {
                Success = false,
                Message = "The dumped file no longer exists.",
            };
        }

        byte[] file = File.ReadAllBytes(filePath);
        if (AssemblyCarver.TryReadAssemblyName(file, out _, out _))
        {
            return new MetadataRepairResult
            {
                Success = true,
                Changed = false,
                Message = "Metadata is already readable; no repair was needed.",
                OutputPath = filePath,
            };
        }

        if (!PeImageLayout.TryReadHeaders(file, out PeHeaders headers))
        {
            return new MetadataRepairResult
            {
                Success = false,
                Message = "The dump has no usable PE header; this needs carving, not metadata repair.",
            };
        }

        using RemoteProcess? target = RemoteProcess.TryOpen(processId, out string? openError);
        if (target == null)
        {
            return new MetadataRepairResult
            {
                Success = false,
                Message = "The target process is no longer readable: " + (openError ?? "unknown error"),
            };
        }

        try
        {
            using DataTarget dataTarget = DataTarget.AttachToProcess(processId, false);
            ClrInfo? clrInfo = dataTarget.ClrVersions.FirstOrDefault();
            if (clrInfo == null)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The target no longer hosts a CLR that ClrMD can attach to.",
                };
            }

            using ClrRuntime runtime = clrInfo.CreateRuntime();
            ClrModule? module = FindModule(runtime, imageBase, moduleName);
            if (module == null)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "ClrMD could not find the module in the live runtime.",
                };
            }

            if (module.MetadataAddress == 0 || module.MetadataLength < 0x20 ||
                module.MetadataLength > 64UL * 1024 * 1024)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The runtime does not expose a usable metadata address for this module.",
                };
            }

            ulong baseAddress = module.ImageBase != 0 ? module.ImageBase : imageBase;
            if (module.MetadataAddress < baseAddress)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The metadata lives outside the module image; this module needs a section-level rebuild.",
                };
            }

            ulong metadataRva64 = module.MetadataAddress - baseAddress;
            if (metadataRva64 >= headers.SizeOfImage ||
                metadataRva64 > uint.MaxValue ||
                module.MetadataLength > int.MaxValue)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The metadata address is outside the dumped image.",
                };
            }

            uint metadataRva = (uint)metadataRva64;
            int metadataLength = (int)module.MetadataLength;
            byte[] metadata = target.ReadBytes(module.MetadataAddress, metadataLength, out _);
            if (metadata.Length < 4 || PeImageLayout.ReadU32(metadata, 0) != MetadataSignature)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The runtime metadata is not a BSJB metadata root.",
                };
            }

            int cliOffset = FindCliHeader(file, headers, metadataRva);
            if (cliOffset < 0)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The COR20 (CLI) header could not be located in the dump.",
                };
            }

            int metadataOffset = PeImageLayout.RvaToFileOffset(headers, metadataRva);
            if (metadataOffset < 0 || (long)metadataOffset + metadataLength > file.Length)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The repaired metadata does not fit the existing section layout.",
                };
            }

            // Always copy the runtime metadata blob. The on-disk copy may still
            // start with BSJB while a stream header was wiped, so a signature
            // check alone is not enough to trust it.
            Buffer.BlockCopy(metadata, 0, file, metadataOffset, metadataLength);

            PeImageLayout.WriteU32(file, cliOffset + 8, metadataRva);
            PeImageLayout.WriteU32(file, cliOffset + 12, (uint)metadataLength);

            int directoryOffset = GetClrDirectoryOffset(headers);
            if (directoryOffset < 0 || directoryOffset + 8 > file.Length)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The PE optional header has no CLR data directory.",
                };
            }

            uint cliRva = FileOffsetToRva(headers, cliOffset, headers.SizeOfImage);
            if (cliRva == 0)
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The COR20 header is not inside a mapped section.",
                };
            }

            PeImageLayout.WriteU32(file, directoryOffset, cliRva);
            PeImageLayout.WriteU32(file, directoryOffset + 4, Cor20HeaderSize);

            if (!AssemblyCarver.TryReadAssemblyName(file, out _, out _))
            {
                return new MetadataRepairResult
                {
                    Success = false,
                    Message = "The repaired image is still not a readable .NET assembly.",
                };
            }

            File.WriteAllBytes(filePath, file);
            return new MetadataRepairResult
            {
                Success = true,
                Changed = true,
                Message = $"Metadata repaired from the live CLR ({metadataLength:N0} bytes at RVA 0x{metadataRva:X}).",
                OutputPath = filePath,
            };
        }
        catch (Exception ex)
        {
            return new MetadataRepairResult
            {
                Success = false,
                Message = "Metadata repair failed: " + ex.Message,
            };
        }
    }

    private static ClrModule? FindModule(ClrRuntime runtime, ulong imageBase, string? moduleName)
    {
        ClrModule? byBase = runtime.EnumerateModules()
            .FirstOrDefault(module => module.ImageBase != 0 && module.ImageBase == imageBase);
        if (byBase != null)
            return byBase;

        if (string.IsNullOrWhiteSpace(moduleName))
            return null;

        string wanted = Path.GetFileName(moduleName);
        return runtime.EnumerateModules().FirstOrDefault(module =>
            string.Equals(Path.GetFileName(module.Name ?? string.Empty), wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(module.AssemblyName ?? string.Empty), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetClrDirectoryOffset(PeHeaders headers)
        => headers.OptionalHeaderOffset + (headers.Is64Bit ? 112 : 96) + 14 * 8;

    private static int FindCliHeader(byte[] file, PeHeaders headers, uint metadataRva)
    {
        int directoryOffset = GetClrDirectoryOffset(headers);
        if (directoryOffset + 8 <= file.Length)
        {
            uint cliRva = PeImageLayout.ReadU32(file, directoryOffset);
            if (cliRva != 0)
            {
                int direct = PeImageLayout.RvaToFileOffset(headers, cliRva);
                if (direct >= 0 && direct + Cor20HeaderSize <= file.Length &&
                    PeImageLayout.ReadU32(file, direct) >= Cor20HeaderSize)
                    return direct;
            }
        }

        // The directory is often zeroed by anti-dump code while the COR20 header
        // itself survives. Scan raw section bytes for a header whose metadata RVA
        // matches the address reported by the CLR.
        foreach (PeSection section in headers.Sections)
        {
            if (section.RawSize < Cor20HeaderSize || section.RawPointer == 0)
                continue;

            long end = (long)section.RawPointer + section.RawSize;
            if (end > file.Length)
                end = file.Length;
            int start = (int)section.RawPointer;
            if (start < 0)
                continue;

            for (int offset = start; offset + Cor20HeaderSize <= end; offset += 4)
            {
                if (PeImageLayout.ReadU32(file, offset) != Cor20HeaderSize)
                    continue;
                if (PeImageLayout.ReadU16(file, offset + 4) != 2)
                    continue;

                uint candidateRva = PeImageLayout.ReadU32(file, offset + 8);
                uint candidateSize = PeImageLayout.ReadU32(file, offset + 12);
                if (candidateRva != metadataRva || candidateSize == 0)
                    continue;
                if (candidateRva >= headers.SizeOfImage)
                    continue;

                return offset;
            }
        }

        return -1;
    }

    private static uint FileOffsetToRva(PeHeaders headers, int fileOffset, uint sizeOfImage)
    {
        foreach (PeSection section in headers.Sections)
        {
            if (section.RawSize == 0)
                continue;
            long start = section.RawPointer;
            long end = start + section.RawSize;
            if (fileOffset < start || fileOffset >= end)
                continue;

            long rva = (long)section.VirtualAddress + (fileOffset - start);
            if (rva <= 0 || rva >= sizeOfImage)
                return 0;
            return (uint)rva;
        }

        return 0;
    }
}
