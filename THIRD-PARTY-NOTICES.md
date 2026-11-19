# Third-party notices

OBSIDIAN bundles or references the following components. Their licences apply to
the respective binaries and source files.

## dnlib 4.4.0

- Project: https://github.com/0xd4d/dnlib
- Licence: MIT
- Usage: reading, writing and verifying .NET metadata in the dumper and the
  inspector.

## ICSharpCode.Decompiler (ILSpy) 9.1.0

- Project: https://github.com/icsharpcode/ILSpy
- Licence: MIT
- Usage: C# decompilation of dumped assemblies.

## Microsoft.Diagnostics.Runtime (ClrMD) 4.2

- Project: https://github.com/microsoft/clrmd
- Licence: MIT
- Usage: managed heap/runtime snapshots and anti-dump metadata repair.

ClrMD pulls the following transitive NuGet dependencies, which are also MIT
licensed and are shipped next to the application in the self-contained build:
Microsoft.Diagnostics.NETCore.Client, Microsoft.Extensions.*,
Microsoft.Identity.Client, Azure.Core, Azure.Identity, System.ClientModel,
System.Collections.Immutable, System.Reflection.Metadata.

## .NET runtime

- Project: https://github.com/dotnet/runtime
- Licence: MIT
- Usage: WPF application runtime and base class library. The portable
  (self-contained) build redistributes the .NET 10 runtime and the Windows
  Desktop runtime components.

## Fonts

The UI uses Segoe UI Variable / Segoe UI, Cascadia Mono (where installed) and
Segoe MDL2 Assets for icon glyphs. They ship with Windows and are not
redistributed with this project.
