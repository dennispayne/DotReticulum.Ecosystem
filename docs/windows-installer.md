# Windows MSI

The repository can build one unsigned, x64, per-machine MSI with a WiX feature
tree for `lxmd`, `rnphone`, `nomadnet` and `sideband`. All four
application features are selected by default and can be deselected
independently. App files are installed under `Program Files\DotReticulum
Ecosystem`; shortcuts are placed in the all-users Start Menu.

The MSI contains self-contained Windows NativeAOT app outputs, each app's
reference documentation and third-party notices, and the repository's existing
`LICENSE`. It installs no Reticulum service, does not create or alter RNS
configuration, and does not remove app data during uninstall. The `rnphone`
feature includes only the verified UCRT64 codec runtime DLLs it needs, their
license texts, and the exact Codec2 source archive, MSYS2 patch and pinned
package recipe. The regular per-app ZIPs and other platforms remain unchanged;
see [LXST's native dependency record](lxst.md#native-codec-and-platform-matrix).

## Building

Run on Windows with the .NET 10 SDK, Visual Studio's MSVC NativeAOT tools,
PowerShell 7 and network access to the pinned MSYS2 package/source URLs:

```powershell
pwsh -NoProfile -File tools\Build-WindowsMsi.ps1 `
  -Version 1.0.0 `
  -OutputDirectory artifacts\builds\windows-msi-1.0.0
```

The output directory must not already exist. The script publishes all four
applications with linker and NativeAOT warnings treated as errors, stages
notices and runtime dependencies, verifies downloaded archives against pinned
SHA-256 values, and builds the WiX 4.0.6 package. The packaging project is
deliberately separate from `DotReticulum.Ecosystem.slnx`, so ordinary solution
builds do not depend on staged installer payloads. The MSI version is an
independent three-part Windows Installer version, not an application's
four-part Nerdbank.GitVersioning version.

The MSI is not signed and is not included in the tag-driven release workflow.
This is a local Windows installer; it has not been published as an official
release artifact. Although the Codec2 LGPL source, patch and recipe are
included, the matching source for the bundled `libgcc_s_seh-1.dll` is not.
External redistribution of this MSI is deferred until that source and license
obligation is resolved.

## Acceptance evidence

The sandbox build and lifecycle evidence below was recorded for the earlier
five-feature package, which also contained a since-removed standalone
`telemetry` application. The current four-feature authoring has not been
rebuilt or re-run through that sandbox lifecycle.

`pwsh -NoProfile -File tests\run-pester.ps1 -Path tests\Pester\WindowsMsi.Tests.ps1`
passes 5/5 tests. The WiX 4.0.6 build succeeds with zero warnings and errors.
The final payload was built with
`pwsh -NoProfile -File tools\Build-WindowsMsi.ps1 -Version 1.0.0 -OutputDirectory artifacts\builds\windows-msi-final`;
the build completed with zero warnings and errors.
A fresh Windows Sandbox install of the final 1.0.0 MSI succeeded with all five
default features. Each installed executable's `--help` flow exited 0, all five
Start Menu shortcuts were present, and no Reticulum/application Windows service
was created. The included Codec2 source archive, patch and recipe were
hash-verified after installation.

In the same fresh sandbox, repair restored a removed `telemetry.exe`; a
test-only 1.0.1 package upgraded the installation and registered version 1.0.1;
full uninstall removed the app files while preserving sentinels in the
Reticulum roaming profile, Sideband local profile and ProgramData. A subsequent
`ADDLOCAL=Telemetry,Sideband` install contained only those two app executables
and shortcuts; its uninstall also preserved the sentinels. These are Windows
Installer lifecycle checks, not audio-device or live Reticulum interoperability
tests. The packaged `rnphone` codec DLLs were verified present and intact, but
an audio call through the installed MSI was not exercised.

Final local MSI SHA-256:

| Artifact | SHA-256 |
| --- | --- |
| `DotReticulum.Ecosystem-1.0.0-win-x64.msi` | `c16e7ef15ef536861f0c8eba79fe0a6822471c33fa82faadad3b31a0b6908b37` |
| Test-only upgrade package `DotReticulum.Ecosystem-1.0.1-win-x64.msi` | `dd27418c9b0685177320d69494e8053438aefb6c804363da33f8fe480f9d677d` |

The corresponding local build and sandbox logs are under the ignored
`artifacts\builds\windows-msi-final` and `artifacts\tests\windows-msi-final-*`
directories. No release workflow, signing identity, other-OS installer, or
physical audio test is claimed. The lifecycle results establish installer
behavior, not legal clearance for external distribution.
