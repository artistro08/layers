# Layers WinUI 3 Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Rust Layers 1.0.3 tray app with a C# WinUI 3 / Windows App SDK app that has full feature parity, ships as a signed MSI and a signed MSIX, and has tests for every part.

**Architecture:** Four projects. `Layers.Core` holds pure logic, Windows services (HID, registry, GDI icon rendering), and view models with no XAML, so plain MSTest can cover it. `Layers.UI` is a WinUI class library with the windows (tray icon host, menu, HUD, settings). `Layers` is a thin exe that wires everything together. `Layers.Installer` is a WiX MSI project.

**Tech Stack:** .NET 10 (SDK 10.0.401), Windows App SDK 2.5.1, CsWin32 0.3.335, System.IO.Hashing 10.0.12, MSTest 4.4.1, Microsoft.NET.Test.Sdk 18.10.1, Microsoft.Extensions.TimeProvider.Testing 10.10.0, WiX Toolset 7.0.0, Windows SDK 10.0.26100 (`signtool`).

**Spec:** `docs/superpowers/specs/2026-09-25-winui3-rewrite-design.md`. Read it before starting any task.

**Rust reference:** the Rust source stays readable in git history at `ac9897a` (`git show ac9897a:src/protocol.rs`). Task 1 deletes it from the working tree.

## Global Constraints

- Branch: `winui-rewrite`. **Do not commit.** The repo owner commits. Reviewers review the working-tree diff (`git status`, `git diff`, plus new untracked files).
- Target framework: `net10.0-windows10.0.26100.0`. `TargetPlatformMinVersion` and `SupportedOSPlatformVersion` are `10.0.19041.0`. Platform `x64` only, runtime identifier `win-x64`.
- `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `GenerateDocumentationFile=true` in all projects. `IsAotCompatible=true` in `Layers.Core`, `Layers.UI`, and `Layers`.
- Native AOT: no reflection-based serialization, no `Type.GetType`, no `dynamic`. JSON isn't used anywhere.
- Code style (from the owner's global rules):
  - 4-space indents in C# and XAML.
  - A Title Case comment label above every logical block (`// Build The Packet`), with no trailing period.
  - Aligned `=` in groups of related assignments.
  - Guard clauses first.
  - Section banners in long classes (`// =====` / `// NAME` / `// =====`).
  - A full XML doc comment on every public type and member: `<summary>`, a `<remarks>` paragraph saying what it does and why, then `<param>` / `<returns>` / `<exception>`.
- XAML: one attribute per line on elements with more than 2 attributes, and a comment label before each block (`<!-- Status Row -->`).
- US English everywhere.
- Every string a user sees is copied **verbatim** from the spec. Don't reword any of them.
- Versions are pinned in `Directory.Packages.props` (central package management). Don't add packages that aren't listed there.
- Win32 calls go through CsWin32 (`src/Layers.Core/NativeMethods.txt`, generated as `public`). No hand-written `[DllImport]`.
- Types in the exe project (`Layers`) are `internal` (CA1515).
- Run the full test suite with `dotnet test Layers.slnx -c Debug --filter "TestCategory!=Hardware"` from `D:\layers`.

## Review Focus

These are the inputs and failure modes the spec implies that are most likely to bite a real user. Each one has a test pinned in the named task.

1. **The web config tool wipes our expression mid-session.** The layer display must recover within about 4s without writing during the tool's save burst. Pinned in Task 7: `Reverify_TwoMissesReinstallAndResetLayer` and `Reverify_OneMissDoesNothing`.
2. **A damaged or short report from the device.** It must be skipped, never crash the app, and never read out of range. Pinned in Task 3: `TryParseMonitorReport_TruncatedIsFalse` and the related tests. Also Task 7: `LiveReports_MalformedIgnored`.
3. **The device is unplugged and replugged, or the session throws.** The app must show Disconnected, then reconnect without a restart. Pinned in Task 7: `Departed_ShowsDisconnectedAndArrivedReconnects` and `SessionError_RetriesAfterDelay`.
4. **Explorer restarts, or the taskbar theme or DPI changes.** The icon must come back with the right color and size. Pinned in Task 9: `Route_TaskbarCreatedReAdds` and `RenderBgra_TintFollowsTaskbarTheme`. Also Task 11's manual check.
5. **A leak in the icon's Windows drawing objects over a long session.** Layer switching runs for days. Pinned in Task 9: `RenderAndCreate_ThousandTimesDoesNotLeakGdi`.

---

## File Map

```
Layers.slnx
Directory.Build.props
Directory.Packages.props
.editorconfig
.gitignore                                   (replaced)
build.ps1
README.md                                    (updated in Task 17)
src/Layers.Core/Layers.Core.csproj
src/Layers.Core/NativeMethods.txt
src/Layers.Core/NativeMethods.json
src/Layers.Core/Logic/LayerMask.cs
src/Layers.Core/Logic/DeviceStatus.cs         DeviceStatus, DeviceState, StatusText
src/Layers.Core/Logic/Protocol.cs             Protocol, SlotContents, SlotChoice, TryParse<T>
src/Layers.Core/Logic/HudRules.cs             HudSettings, HudRules
src/Layers.Core/Logic/HudTiming.cs
src/Layers.Core/Logic/HudPlacement.cs
src/Layers.Core/Logic/IconMath.cs             AlphaBuffer, IconMath
src/Layers.Core/Services/SettingsStore.cs
src/Layers.Core/Services/TaskbarTheme.cs
src/Layers.Core/Services/StartupRegistration.cs   IStartupRegistration, StartupState, RunKeyStartup, PackagedStartupTask
src/Layers.Core/Services/AppPackaging.cs
src/Layers.Core/Services/Hid.cs               IHidChannel, IHidDeviceSource, HidDevicePair, DeviceProtocolException
src/Layers.Core/Services/DeviceService.cs
src/Layers.Core/Services/WinRtHid.cs          WinRtHidChannel, WinRtHidDeviceSource
src/Layers.Core/Services/TrayIconRenderer.cs
src/Layers.Core/Services/TrayMessageRouter.cs
src/Layers.Core/ViewModels/ObservableObject.cs
src/Layers.Core/ViewModels/TrayMenuViewModel.cs
src/Layers.Core/ViewModels/SettingsViewModel.cs   SettingsViewModel, LayerOptionViewModel
src/Layers.UI/Layers.UI.csproj
src/Layers.UI/TrayIcon.cs
src/Layers.UI/TrayMenuHost.xaml(.cs)
src/Layers.UI/HudWindow.xaml(.cs)
src/Layers.UI/SettingsWindow.xaml(.cs)
src/Layers.UI/WindowStyles.cs
src/Layers/Layers.csproj
src/Layers/Program.cs
src/Layers/App.xaml(.cs)
src/Layers/app.manifest
src/Layers/Package.appxmanifest
src/Layers/Assets/*.png                       MSIX logos
src/Layers.Installer/Layers.Installer.wixproj
src/Layers.Installer/Package.wxs
tests/Layers.Tests/Layers.Tests.csproj
tests/Layers.Tests/*Tests.cs
tests/Layers.Tests/Fakes/FakeFirmware.cs      FakeHidChannel + FakeHidDeviceSource
tests/Layers.Tests/Fakes/Replies.cs
tests/Layers.Tests/Fakes/Eventually.cs
tests/Layers.Tests/TempRegistryKey.cs
tests/Layers.UITests/Layers.UITests.csproj
tests/Layers.UITests/UiHost.cs
tests/Layers.UITests/*Tests.cs
```

---

### Task 1: Solution Scaffold and Rust Removal

**Files:**
- Delete: `src/*.rs`, `Cargo.toml`, `Cargo.lock` (if present), `build.rs`, `app.manifest`, `installer/layers.iss`, `assets/NOTICE-fluentui.txt`
- Replace: `.gitignore`
- Create: `Layers.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `src/Layers.Core/Layers.Core.csproj`, `src/Layers.Core/NativeMethods.txt`, `src/Layers.Core/NativeMethods.json`, `tests/Layers.Tests/Layers.Tests.csproj`, `tests/Layers.Tests/ScaffoldTests.cs`

**Interfaces:**
- Produces: buildable `Layers.Core` (namespace root `Layers.Core`) and `Layers.Tests` (namespace root `Layers.Tests`). CsWin32 types are generated `public` in namespace `Windows.Win32`.

- [ ] **Step 1: Delete the Rust sources and the old installer**

```bash
cd /d/layers
git rm -q src/*.rs Cargo.toml build.rs app.manifest installer/layers.iss assets/NOTICE-fluentui.txt
git rm -q --ignore-unmatch Cargo.lock
```

- [ ] **Step 2: Replace `.gitignore`**

```gitignore
# Build Output
bin/
obj/
dist/
AppPackages/
BundleArtifacts/

# IDE
.vs/
*.user

# Tooling
.superpowers/
TestResults/
```

- [ ] **Step 3: Create `Directory.Build.props`**

```xml
<Project>
    <!-- Version (single source for exe, MSI, MSIX) -->
    <PropertyGroup>
        <Version>2.0.0</Version>
        <Authors>Devin Green</Authors>
        <Product>Layers</Product>
    </PropertyGroup>

    <!-- Target (C# projects only; the WiX project sets its own) -->
    <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.csproj'">
        <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
        <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
        <Platforms>x64</Platforms>
        <PlatformTarget>x64</PlatformTarget>
        <RuntimeIdentifier>win-x64</RuntimeIdentifier>
        <LangVersion>latest</LangVersion>
        <ImplicitUsings>enable</ImplicitUsings>
    </PropertyGroup>

    <!-- Standards (C# projects only) -->
    <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.csproj'">
        <Nullable>enable</Nullable>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <AnalysisLevel>latest-recommended</AnalysisLevel>
        <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
    </PropertyGroup>

    <!-- Central Package Versions -->
    <PropertyGroup>
        <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create `Directory.Packages.props`**

```xml
<Project>
    <!-- Pinned Versions -->
    <ItemGroup>
        <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
        <PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.335" />
        <PackageVersion Include="System.IO.Hashing" Version="10.0.12" />
        <PackageVersion Include="MSTest.TestFramework" Version="4.4.1" />
        <PackageVersion Include="MSTest.TestAdapter" Version="4.4.1" />
        <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
        <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    </ItemGroup>
</Project>
```

Before continuing, confirm these are still the latest stable versions: `dotnet package search <id> --exact-match`. If a newer stable version exists, pin that instead and note it in your report.

- [ ] **Step 5: Create `.editorconfig`**

```ini
root = true

# All Files
[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

# Formatter-Owned Files
[*.{css,yaml,yml,json}]
indent_size = 2

# C#
[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
csharp_prefer_braces = true:warning
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
dotnet_style_qualification_for_field = false:warning
dotnet_naming_rule.private_fields_underscore.symbols = private_fields
dotnet_naming_rule.private_fields_underscore.style = underscore_camel
dotnet_naming_rule.private_fields_underscore.severity = warning
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private
dotnet_naming_style.underscore_camel.required_prefix = _
dotnet_naming_style.underscore_camel.capitalization = camel_case
# Aligned '=' in assignment groups is house style
dotnet_diagnostic.IDE0055.severity = none
```

- [ ] **Step 6: Create `src/Layers.Core/Layers.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- Library -->
    <PropertyGroup>
        <RootNamespace>Layers.Core</RootNamespace>
        <IsAotCompatible>true</IsAotCompatible>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    </PropertyGroup>

    <!-- Packages -->
    <ItemGroup>
        <PackageReference Include="Microsoft.Windows.CsWin32" PrivateAssets="all" />
        <PackageReference Include="System.IO.Hashing" />
    </ItemGroup>

    <!-- Test Access -->
    <ItemGroup>
        <InternalsVisibleTo Include="Layers.Tests" />
    </ItemGroup>
</Project>
```

- [ ] **Step 7: Create `src/Layers.Core/NativeMethods.json` and an initial `NativeMethods.txt`**

`NativeMethods.json`:

```json
{
  "$schema": "https://aka.ms/CsWin32.schema.json",
  "public": true,
  "emitSingleFile": false
}
```

`NativeMethods.txt` (later tasks append to it):

```
GetCurrentPackageFullName
APPMODEL_ERROR_NO_PACKAGE
```

- [ ] **Step 8: Create `tests/Layers.Tests/Layers.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- Test Project -->
    <PropertyGroup>
        <RootNamespace>Layers.Tests</RootNamespace>
        <IsPackable>false</IsPackable>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        <!-- Test names use underscores; tests carry no XML docs -->
        <NoWarn>$(NoWarn);CA1707;CS1591;CA1515</NoWarn>
    </PropertyGroup>

    <!-- Packages -->
    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
        <PackageReference Include="MSTest.TestFramework" />
        <PackageReference Include="MSTest.TestAdapter" />
        <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
    </ItemGroup>

    <!-- Projects -->
    <ItemGroup>
        <ProjectReference Include="..\..\src\Layers.Core\Layers.Core.csproj" />
    </ItemGroup>

    <!-- Parallel Test Execution -->
    <ItemGroup>
        <AssemblyAttribute Include="Microsoft.VisualStudio.TestTools.UnitTesting.ParallelizeAttribute">
            <Workers>0</Workers>
            <Workers_IsLiteral>true</Workers_IsLiteral>
            <Scope>Microsoft.VisualStudio.TestTools.UnitTesting.ExecutionScope.MethodLevel</Scope>
            <Scope_IsLiteral>true</Scope_IsLiteral>
        </AssemblyAttribute>
    </ItemGroup>
</Project>
```

- [ ] **Step 9: Write the scaffold smoke test** `tests/Layers.Tests/ScaffoldTests.cs`

```csharp
using Layers.Core.Services;

namespace Layers.Tests;

[TestClass]
public sealed class ScaffoldTests
{
    [TestMethod]
    public void TestHost_IsUnpackaged()
    {
        Assert.IsFalse(AppPackaging.IsPackaged);
    }
}
```

- [ ] **Step 10: Run it to verify it fails**

```bash
dotnet new sln --format slnx -n Layers
dotnet sln Layers.slnx add src/Layers.Core/Layers.Core.csproj tests/Layers.Tests/Layers.Tests.csproj
dotnet test Layers.slnx
```

Expected: build error `CS0246: The type or namespace name 'AppPackaging' could not be found`.

- [ ] **Step 11: Create `src/Layers.Core/Services/AppPackaging.cs`**

```csharp
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Layers.Core.Services;

/// <summary>
/// Reports whether the current process runs with MSIX package identity.
/// </summary>
/// <remarks>
/// The same binary ships unpackaged (MSI) and packaged (MSIX). A few features differ between the two:
/// start at sign-in uses the Run key unpackaged and <c>StartupTask</c> packaged.
/// </remarks>
public static class AppPackaging
{
    /// <summary>
    /// Gets a value indicating whether the process has package identity.
    /// </summary>
    /// <remarks>
    /// <c>GetCurrentPackageFullName</c> returns <c>APPMODEL_ERROR_NO_PACKAGE</c> for unpackaged processes.
    /// Any other result, including a buffer-size error, means a package is present.
    /// </remarks>
    public static unsafe bool IsPackaged
    {
        get
        {
            // Probe With A Zero-Length Buffer
            uint length = 0;
            var result  = PInvoke.GetCurrentPackageFullName(ref length, null);

            return result != WIN32_ERROR.APPMODEL_ERROR_NO_PACKAGE;
        }
    }
}
```

If the CsWin32-generated signature differs (for example it takes a `Span<char>`), call the generated overload. The behavior the test pins stays the same.

- [ ] **Step 12: Run the test to verify it passes**

Run: `dotnet test Layers.slnx`
Expected: `Passed: 1`. No warnings. `TreatWarningsAsErrors` turns any analyzer warning into a failure, so fix any that appear.

---

### Task 2: LayerMask, DeviceStatus, and Status Text

**Files:**
- Create: `src/Layers.Core/Logic/LayerMask.cs`, `src/Layers.Core/Logic/DeviceStatus.cs`
- Test: `tests/Layers.Tests/LayerMaskTests.cs`, `tests/Layers.Tests/StatusTextTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct LayerMask(byte Bits)` with `static LayerMask Base`, `IReadOnlyList<int> Active`, `int? Badge`, and `string Label`.
  - `enum DeviceStatus { Disconnected = 0, NoSlot = 1, Connected = 2, VersionMismatch = 3 }`.
  - `readonly record struct DeviceState(DeviceStatus Status, LayerMask Layers)` with `static DeviceState Initial`.
  - `static class StatusText` with `string Tooltip(DeviceState)`, `string Label(DeviceStatus)`, and `string? Detail(DeviceStatus)`.

- [ ] **Step 1: Write the failing tests** `tests/Layers.Tests/LayerMaskTests.cs`

```csharp
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class LayerMaskTests
{
    [TestMethod]
    public void LayerZero_ShowsNoBadge()
    {
        var mask = new LayerMask(0b1);

        Assert.IsNull(mask.Badge);
        CollectionAssert.AreEqual(new[] { 0 }, mask.Active.ToArray());
        Assert.AreEqual("Layer 0", mask.Label);
    }

    [TestMethod]
    public void EmptyMask_IsTreatedAsLayerZero()
    {
        var mask = new LayerMask(0);

        CollectionAssert.AreEqual(new[] { 0 }, mask.Active.ToArray());
        Assert.IsNull(mask.Badge);
    }

    [TestMethod]
    public void SingleLayer_BadgesThatLayer()
    {
        var mask = new LayerMask(0b1000);

        Assert.AreEqual(3, mask.Badge);
        Assert.AreEqual("Layer 3", mask.Label);
    }

    [TestMethod]
    public void SeveralLayers_BadgeHighestAndListAll()
    {
        var mask = new LayerMask(0b1010);

        CollectionAssert.AreEqual(new[] { 1, 3 }, mask.Active.ToArray());
        Assert.AreEqual(3, mask.Badge);
        Assert.AreEqual("Layers 1, 3", mask.Label);
    }

    [TestMethod]
    public void LayerZeroWithAnother_BadgesHigher()
    {
        var mask = new LayerMask(0b0101);

        CollectionAssert.AreEqual(new[] { 0, 2 }, mask.Active.ToArray());
        Assert.AreEqual(2, mask.Badge);
        Assert.AreEqual("Layers 0, 2", mask.Label);
    }

    [TestMethod]
    public void AllEightBits_AreParsed()
    {
        Assert.AreEqual(7, new LayerMask(0b1000_0000).Badge);
    }

    [TestMethod]
    public void Base_IsLayerZero()
    {
        Assert.AreEqual((byte)1, LayerMask.Base.Bits);
    }
}
```

`tests/Layers.Tests/StatusTextTests.cs`:

```csharp
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class StatusTextTests
{
    [TestMethod]
    [DataRow(DeviceStatus.Disconnected, "HID Remapper disconnected")]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware version")]
    public void Tooltip_ForNonConnectedStatuses(DeviceStatus status, string expected)
    {
        Assert.AreEqual(expected, StatusText.Tooltip(new DeviceState(status, new LayerMask(0b100))));
    }

    [TestMethod]
    public void Tooltip_WhenConnected_IsLayerLabel()
    {
        Assert.AreEqual("Layer 2", StatusText.Tooltip(new DeviceState(DeviceStatus.Connected, new LayerMask(0b100))));
        Assert.AreEqual("Layers 1, 3", StatusText.Tooltip(new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010))));
    }

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected")]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware")]
    [DataRow(DeviceStatus.Disconnected, "Disconnected")]
    public void Label_PerStatus(DeviceStatus status, string expected)
    {
        Assert.AreEqual(expected, StatusText.Label(status));
    }

    [TestMethod]
    public void Detail_OnlyForDegradedStatuses()
    {
        Assert.AreEqual("All 8 expression slots are in use", StatusText.Detail(DeviceStatus.NoSlot));
        Assert.AreEqual("This app supports config version 18", StatusText.Detail(DeviceStatus.VersionMismatch));
        Assert.IsNull(StatusText.Detail(DeviceStatus.Connected));
        Assert.IsNull(StatusText.Detail(DeviceStatus.Disconnected));
    }

    [TestMethod]
    public void Initial_IsDisconnectedAtLayerZero()
    {
        Assert.AreEqual(new DeviceState(DeviceStatus.Disconnected, LayerMask.Base), DeviceState.Initial);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build error, `LayerMask` not found.

- [ ] **Step 3: Implement `src/Layers.Core/Logic/LayerMask.cs`**

```csharp
using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// The set of currently active HID Remapper layers, one bit per layer.
/// </summary>
/// <remarks>
/// Bit <c>n</c> set means layer <c>n</c> is active. The firmware floors an empty mask to layer 0,
/// but a garbled report must not produce an empty display, so an empty mask is read as layer 0 too.
/// All 8 bits are honored even though the rp2040 firmware only uses 4 layers.
/// </remarks>
/// <param name="Bits">The raw mask from the device.</param>
public readonly record struct LayerMask(byte Bits)
{
    /// <summary>
    /// Gets the mask for layer 0 alone.
    /// </summary>
    /// <remarks>
    /// This is the firmware's state after a connect or a RESUME.
    /// </remarks>
    public static LayerMask Base => new(1);

    /// <summary>
    /// Gets the active layer numbers in ascending order.
    /// </summary>
    /// <remarks>
    /// Never empty: an empty mask yields <c>[0]</c>.
    /// </remarks>
    public IReadOnlyList<int> Active
    {
        get
        {
            // Empty Mask Means Layer 0
            if (Bits == 0)
            {
                return [0];
            }

            // Collect Set Bits
            var active = new List<int>(8);
            for (var layer = 0; layer < 8; layer++)
            {
                if ((Bits & (1 << layer)) != 0)
                {
                    active.Add(layer);
                }
            }

            return active;
        }
    }

    /// <summary>
    /// Gets the digit drawn on the tray icon.
    /// </summary>
    /// <remarks>
    /// This is the highest active layer, or <see langword="null"/> when that is layer 0, which draws as the bare glyph.
    /// </remarks>
    public int? Badge
    {
        get
        {
            var highest = Active[^1];
            return highest == 0 ? null : highest;
        }
    }

    /// <summary>
    /// Gets the human-readable label, for example "Layer 2" or "Layers 1, 3".
    /// </summary>
    /// <remarks>
    /// Used by the tooltip, the menu, and the HUD.
    /// </remarks>
    public string Label
    {
        get
        {
            var active = Active;
            var list   = string.Join(", ", active.Select(layer => layer.ToString(CultureInfo.InvariantCulture)));

            return active.Count == 1 ? $"Layer {list}" : $"Layers {list}";
        }
    }
}
```

- [ ] **Step 4: Implement `src/Layers.Core/Logic/DeviceStatus.cs`**

```csharp
namespace Layers.Core.Logic;

/// <summary>
/// Connection state of the HID Remapper.
/// </summary>
/// <remarks>
/// The numeric values match Layers 1.0.3 for easy cross-reference with the Rust history.
/// </remarks>
public enum DeviceStatus
{
    /// <summary>No device, or the last session failed.</summary>
    Disconnected = 0,

    /// <summary>Connected, but all 8 expression slots belong to the user, so the layer can't be read.</summary>
    NoSlot = 1,

    /// <summary>Connected, and the layer is being read.</summary>
    Connected = 2,

    /// <summary>Connected, but the firmware's config version isn't 18, so nothing is written.</summary>
    VersionMismatch = 3,
}

/// <summary>
/// A snapshot of the device status and active layers.
/// </summary>
/// <remarks>
/// Raised by <c>DeviceService</c> and consumed by the tray icon, the menu, and the HUD rules.
/// </remarks>
/// <param name="Status">The connection status.</param>
/// <param name="Layers">The active layers. Meaningful only when <paramref name="Status"/> is Connected.</param>
public readonly record struct DeviceState(DeviceStatus Status, LayerMask Layers)
{
    /// <summary>
    /// Gets the state before any device is seen.
    /// </summary>
    /// <remarks>
    /// Disconnected at layer 0, matching Layers 1.0.3's startup state.
    /// </remarks>
    public static DeviceState Initial => new(DeviceStatus.Disconnected, LayerMask.Base);
}

/// <summary>
/// User-facing strings for each device status.
/// </summary>
/// <remarks>
/// Copied verbatim from Layers 1.0.3. Don't reword.
/// </remarks>
public static class StatusText
{
    /// <summary>
    /// Gets the tray tooltip.
    /// </summary>
    /// <remarks>
    /// When connected, the tooltip is the layer label itself.
    /// </remarks>
    /// <param name="state">The current device state.</param>
    /// <returns>The tooltip text.</returns>
    public static string Tooltip(DeviceState state) => state.Status switch
    {
        DeviceStatus.Disconnected    => "HID Remapper disconnected",
        DeviceStatus.NoSlot          => "Connected, layer unavailable",
        DeviceStatus.VersionMismatch => "Unsupported firmware version",
        _                            => state.Layers.Label,
    };

    /// <summary>
    /// Gets the status row label in the tray menu.
    /// </summary>
    /// <remarks>
    /// Shorter than the tooltip for VersionMismatch, matching the Rust popup.
    /// </remarks>
    /// <param name="status">The device status.</param>
    /// <returns>The label.</returns>
    public static string Label(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected       => "Connected",
        DeviceStatus.NoSlot          => "Connected, layer unavailable",
        DeviceStatus.VersionMismatch => "Unsupported firmware",
        _                            => "Disconnected",
    };

    /// <summary>
    /// Gets the second-line explanation for degraded statuses.
    /// </summary>
    /// <remarks>
    /// Only NoSlot and VersionMismatch have one.
    /// </remarks>
    /// <param name="status">The device status.</param>
    /// <returns>The detail text, or <see langword="null"/>.</returns>
    public static string? Detail(DeviceStatus status) => status switch
    {
        DeviceStatus.NoSlot          => "All 8 expression slots are in use",
        DeviceStatus.VersionMismatch => "This app supports config version 18",
        _                            => null,
    };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 3: Protocol

**Files:**
- Create: `src/Layers.Core/Logic/Protocol.cs`
- Test: `tests/Layers.Tests/ProtocolTests.cs`, `tests/Layers.Tests/Fakes/Replies.cs`

**Interfaces:**
- Consumes: `LayerMask`.
- Produces:
  - `delegate bool TryParse<T>(ReadOnlySpan<byte> packet, out T value)`
  - `sealed record SlotContents(byte ElementCount, byte[] Bytes)` with `bool IsOurs`
  - `enum SlotChoiceKind { Existing, Empty, NoneFree }` and `readonly record struct SlotChoice(SlotChoiceKind Kind, byte Slot)`
  - `static class Protocol` with:
    - constants `ReportIdConfig=100`, `ReportIdMonitor=101`, `ConfigVersion=18`, `PacketLength=33`, `PayloadLength=26`, `ConfigUsagePage=0xFF00`, `ConfigUsage=0x0020`, `MonitorUsage=0x0021`, `ExpressionSlots=8`, `SentinelUsage=0xFF000001`, `MonitorReportLength=64`
    - command constants `CmdGetConfig=3`, `CmdResume=11`, `CmdAppendToExpression=20`, `CmdGetExpression=21`, `CmdSetMonitorEnabled=22`
    - `ExpressionBytes`
    - `byte[] BuildPacket(byte, ReadOnlySpan<byte>)`, `bool VerifyCrc(ReadOnlySpan<byte>)`
    - `byte[] GetConfig()`, `GetExpression(byte)`, `AppendExpression(byte)`, `Resume()`, `SetMonitorEnabled(bool)`
    - `bool TryParseConfigVersion(ReadOnlySpan<byte>, out byte)`, `bool TryParseExpressionResponse(ReadOnlySpan<byte>, out SlotContents)`, `bool TryParseMonitorReport(ReadOnlySpan<byte>, out LayerMask)`
    - `SlotChoice ChooseSlot(IReadOnlyList<SlotContents>)`
  - Test helper `static class Replies` with `byte[] Config(byte version)`, `byte[] Expression(byte count, ReadOnlySpan<byte> bytes)`, and `byte[] Monitor(params (uint Usage, int Value)[] items)`

- [ ] **Step 1: Write the test helper** `tests/Layers.Tests/Fakes/Replies.cs`

```csharp
using System.Buffers.Binary;
using System.IO.Hashing;
using Layers.Core.Logic;

namespace Layers.Tests.Fakes;

/// Builds device replies the way HID Remapper firmware does.
internal static class Replies
{
    public static byte[] Config(byte version)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = version;
        return Sign(packet);
    }

    public static byte[] Expression(byte count, ReadOnlySpan<byte> bytes)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = count;
        bytes.CopyTo(packet.AsSpan(2));
        return Sign(packet);
    }

    public static byte[] Monitor(params (uint Usage, int Value)[] items)
    {
        var report = new byte[Protocol.MonitorReportLength];
        report[0]  = Protocol.ReportIdMonitor;
        for (var i = 0; i < items.Length; i++)
        {
            var offset = 1 + i * 9;
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(offset), items[i].Usage);
            BinaryPrimitives.WriteInt32LittleEndian(report.AsSpan(offset + 4), items[i].Value);
        }
        return report;
    }

    private static byte[] Sign(byte[] packet)
    {
        var crc = Crc32.HashToUInt32(packet.AsSpan(1, 28));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(29), crc);
        return packet;
    }
}
```

Add `<PackageReference Include="System.IO.Hashing" />` to `Layers.Tests.csproj`.

- [ ] **Step 2: Write the failing tests** `tests/Layers.Tests/ProtocolTests.cs` (ports the 36 `protocol.rs` tests)

```csharp
using System.Buffers.Binary;
using System.IO.Hashing;
using Layers.Core.Logic;
using Layers.Tests.Fakes;

namespace Layers.Tests;

[TestClass]
public sealed class ProtocolTests
{
    private static readonly byte[] Ours = Protocol.ExpressionBytes.ToArray();

    // =========================================================================
    // FRAMING
    // =========================================================================

    [TestMethod]
    public void Packet_HasHeaderAndLength()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdResume, []);

        Assert.AreEqual(Protocol.PacketLength, packet.Length);
        Assert.AreEqual(Protocol.ReportIdConfig, packet[0]);
        Assert.AreEqual(Protocol.ConfigVersion, packet[1]);
        Assert.AreEqual(Protocol.CmdResume, packet[2]);
        Assert.IsTrue(packet[3..29].All(b => b == 0));
    }

    [TestMethod]
    public void Packet_PayloadFollowsCommandByte()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdSetMonitorEnabled, [1]);

        Assert.AreEqual((byte)1, packet[3]);
        Assert.IsTrue(packet[4..29].All(b => b == 0));
    }

    [TestMethod]
    public void Crc_CoversBytesOneThroughTwentyEight()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdResume, []);

        Assert.AreEqual(Crc32.HashToUInt32(packet.AsSpan(1, 28)), BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(29)));
    }

    [TestMethod]
    public void VerifyCrc_AcceptsOwnPackets()
    {
        Assert.IsTrue(Protocol.VerifyCrc(Protocol.BuildPacket(Protocol.CmdGetExpression, [3, 0, 0, 0])));
    }

    [TestMethod]
    public void VerifyCrc_RejectsCorruptedPayload()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdGetExpression, [3, 0, 0, 0]);
        packet[5] ^= 0xFF;

        Assert.IsFalse(Protocol.VerifyCrc(packet));
    }

    [TestMethod]
    public void VerifyCrc_RejectsShortBuffer()
    {
        Assert.IsFalse(Protocol.VerifyCrc(new byte[10]));
    }

    [TestMethod]
    public void OversizedPayload_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Protocol.BuildPacket(Protocol.CmdResume, new byte[27]));
    }

    // =========================================================================
    // COMMANDS
    // =========================================================================

    [TestMethod]
    public void ExpressionBytes_AreLayerStatePushSentinelMonitor()
    {
        CollectionAssert.AreEqual(new byte[] { 20, 1, 0x01, 0x00, 0x00, 0xFF, 44 }, Ours);
    }

    [TestMethod]
    public void Sentinel_InExpressionMatchesFilter()
    {
        Assert.AreEqual(Protocol.SentinelUsage, BinaryPrimitives.ReadUInt32LittleEndian(Ours.AsSpan(2)));
    }

    [TestMethod]
    public void Append_CarriesSlotCountAndBytes()
    {
        var packet = Protocol.AppendExpression(5);

        Assert.AreEqual(Protocol.CmdAppendToExpression, packet[2]);
        Assert.AreEqual((byte)5, packet[3]);
        Assert.AreEqual((byte)3, packet[4]);
        CollectionAssert.AreEqual(Ours, packet[5..12]);
        Assert.IsTrue(packet[12..29].All(b => b == 0));
        Assert.IsTrue(Protocol.VerifyCrc(packet));
    }

    [TestMethod]
    public void GetExpression_CarriesSlotAndZeroOffset()
    {
        var packet = Protocol.GetExpression(6);

        Assert.AreEqual(Protocol.CmdGetExpression, packet[2]);
        Assert.AreEqual(6u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7)));
    }

    [TestMethod]
    public void MonitorEnable_DiffersOnlyInPayload()
    {
        Assert.AreEqual((byte)1, Protocol.SetMonitorEnabled(true)[3]);
        Assert.AreEqual((byte)0, Protocol.SetMonitorEnabled(false)[3]);
        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Protocol.SetMonitorEnabled(true)[2]);
    }

    [TestMethod]
    public void Resume_HasEmptyPayload()
    {
        var packet = Protocol.Resume();

        Assert.AreEqual(Protocol.CmdResume, packet[2]);
        Assert.IsTrue(packet[3..29].All(b => b == 0));
    }

    [TestMethod]
    public void GetConfig_HasCommandThree()
    {
        Assert.AreEqual((byte)3, Protocol.GetConfig()[2]);
    }

    // =========================================================================
    // EXPRESSION RESPONSES
    // =========================================================================

    [TestMethod]
    public void ParsesEmptySlot()
    {
        Assert.IsTrue(Protocol.TryParseExpressionResponse(Replies.Expression(0, []), out var slot));
        Assert.AreEqual((byte)0, slot.ElementCount);
    }

    [TestMethod]
    public void ParsesOurExpressionBack()
    {
        Assert.IsTrue(Protocol.TryParseExpressionResponse(Replies.Expression(3, Ours), out var slot));
        Assert.AreEqual((byte)3, slot.ElementCount);
        CollectionAssert.AreEqual(Ours, slot.Bytes[..7]);
        Assert.IsTrue(slot.IsOurs);
    }

    [TestMethod]
    public void ExpressionResponse_BadCrcRejected()
    {
        var reply = Replies.Expression(3, Ours);
        reply[4] ^= 0xFF;

        Assert.IsFalse(Protocol.TryParseExpressionResponse(reply, out _));
    }

    [TestMethod]
    public void ExpressionResponse_ShortRejected()
    {
        Assert.IsFalse(Protocol.TryParseExpressionResponse(new byte[20], out _));
    }

    // =========================================================================
    // SLOT CHOICE
    // =========================================================================

    private static SlotContents Slot(byte count, params byte[] bytes)
    {
        var padded = new byte[27];
        bytes.CopyTo(padded, 0);
        return new SlotContents(count, padded);
    }

    [TestMethod]
    public void ReusesOurSlot_EvenWithEarlierEmpty()
    {
        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Existing, 1), Protocol.ChooseSlot([Slot(0), Slot(3, Ours)]));
    }

    [TestMethod]
    public void TakesFirstEmpty_WhenOursAbsent()
    {
        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Empty, 1), Protocol.ChooseSlot([Slot(5, 20, 20, 20), Slot(0), Slot(0)]));
    }

    [TestMethod]
    public void NoneFree_WhenAllOccupied()
    {
        var slots = Enumerable.Range(0, 8).Select(_ => Slot(2, 20, 44)).ToList();

        Assert.AreEqual(SlotChoiceKind.NoneFree, Protocol.ChooseSlot(slots).Kind);
    }

    [TestMethod]
    public void LongerExpressionStartingLikeOurs_IsNotOurs()
    {
        var bytes = Ours.Append((byte)20).ToArray();

        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Empty, 1), Protocol.ChooseSlot([Slot(4, bytes), Slot(0)]));
    }

    // =========================================================================
    // CONFIG VERSION
    // =========================================================================

    [TestMethod]
    public void ReadsConfigVersion()
    {
        Assert.IsTrue(Protocol.TryParseConfigVersion(Replies.Config(18), out var version));
        Assert.AreEqual((byte)18, version);
    }

    [TestMethod]
    public void ReportsMismatchedVersion()
    {
        Assert.IsTrue(Protocol.TryParseConfigVersion(Replies.Config(19), out var version));
        Assert.AreEqual((byte)19, version);
    }

    [TestMethod]
    public void ConfigResponse_BadCrcRejected()
    {
        var reply = Replies.Config(18);
        reply[1] = 19;

        Assert.IsFalse(Protocol.TryParseConfigVersion(reply, out _));
    }

    // =========================================================================
    // MONITOR REPORTS
    // =========================================================================

    [TestMethod]
    public void ExtractsMaskFromSentinel()
    {
        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, 0b100)), out var mask));
        Assert.AreEqual((byte)0b100, mask.Bits);
    }

    [TestMethod]
    public void FindsSentinelInLastItem()
    {
        var items = Enumerable.Range(0, 6).Select(i => (0x0009_0001u + (uint)i, 1)).Append((Protocol.SentinelUsage, 2)).ToArray();

        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor(items), out var mask));
        Assert.AreEqual((byte)2, mask.Bits);
    }

    [TestMethod]
    public void IgnoresOrdinaryUsages()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor((0x0009_0001u, 1), (0x0001_0030u, -5)), out _));
    }

    [TestMethod]
    public void IgnoresPaddingItems()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor(), out _));
    }

    [TestMethod]
    public void IgnoresWrongReportId()
    {
        var report = Replies.Monitor((Protocol.SentinelUsage, 3));
        report[0] = Protocol.ReportIdConfig;

        Assert.IsFalse(Protocol.TryParseMonitorReport(report, out _));
    }

    [TestMethod]
    public void TryParseMonitorReport_TruncatedIsFalse()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport([Protocol.ReportIdMonitor, 0, 0], out _));
        Assert.IsFalse(Protocol.TryParseMonitorReport([], out _));
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, 3))[..63], out _));
    }

    [TestMethod]
    public void KeepsLowEightBitsOfNegativeValue()
    {
        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, -1)), out var mask));
        Assert.AreEqual((byte)0xFF, mask.Bits);
    }

    [TestMethod]
    public void LongerReport_IsAccepted()
    {
        var report = Replies.Monitor((Protocol.SentinelUsage, 4)).Concat(new byte[8]).ToArray();

        Assert.IsTrue(Protocol.TryParseMonitorReport(report, out var mask));
        Assert.AreEqual((byte)4, mask.Bits);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors, `Protocol` not found.

- [ ] **Step 4: Implement `src/Layers.Core/Logic/Protocol.cs`**

```csharp
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Layers.Core.Logic;

/// <summary>
/// Parses a packet into a value without throwing.
/// </summary>
/// <remarks>
/// Every device reply is untrusted, so parsers report failure rather than throw.
/// </remarks>
/// <typeparam name="T">The parsed value type.</typeparam>
/// <param name="packet">The raw bytes.</param>
/// <param name="value">The parsed value when this returns <see langword="true"/>.</param>
/// <returns><see langword="true"/> when the packet is well formed.</returns>
public delegate bool TryParse<T>(ReadOnlySpan<byte> packet, out T value);

/// <summary>
/// One expression slot as the firmware reports it.
/// </summary>
/// <remarks>
/// Holds an element count and the first 27 encoded element bytes.
/// </remarks>
/// <param name="ElementCount">Number of expression elements in the slot. Zero means the slot is empty.</param>
/// <param name="Bytes">The 27 element bytes from the reply.</param>
public sealed record SlotContents(byte ElementCount, byte[] Bytes)
{
    /// <summary>
    /// Gets a value indicating whether this slot holds our injected expression.
    /// </summary>
    /// <remarks>
    /// Checked periodically, because loading a config from the web tool sends <c>CLEAR_EXPRESSIONS</c>,
    /// which wipes ours along with everything else.
    /// </remarks>
    public bool IsOurs =>
        ElementCount == Protocol.ExpressionElementCount
        && Bytes.Length >= Protocol.ExpressionBytes.Length
        && Bytes.AsSpan(0, Protocol.ExpressionBytes.Length).SequenceEqual(Protocol.ExpressionBytes);
}

/// <summary>
/// Which slot to use for our expression.
/// </summary>
/// <remarks>
/// Existing means ours is already installed, Empty means a free slot to append into,
/// and NoneFree means all 8 slots belong to the user.
/// </remarks>
public enum SlotChoiceKind
{
    /// <summary>Our expression is already in <see cref="SlotChoice.Slot"/>.</summary>
    Existing,

    /// <summary><see cref="SlotChoice.Slot"/> is empty and free to append into.</summary>
    Empty,

    /// <summary>Every slot is in use by the user.</summary>
    NoneFree,
}

/// <summary>
/// The result of <see cref="Protocol.ChooseSlot"/>.
/// </summary>
/// <remarks>
/// <see cref="Slot"/> is meaningless when <see cref="Kind"/> is NoneFree.
/// </remarks>
/// <param name="Kind">What kind of slot was found.</param>
/// <param name="Slot">The slot index, 0 through 7.</param>
public readonly record struct SlotChoice(SlotChoiceKind Kind, byte Slot);

/// <summary>
/// HID Remapper config wire format for config version 18.
/// </summary>
/// <remarks>
/// Pure data, no I/O. Ported from <c>protocol.rs</c> in Layers 1.0.3. Every opcode, report ID, and command byte
/// is tied to config version 18. The app never sends PERSIST_CONFIG, CLEAR_EXPRESSIONS, or SUSPEND,
/// so it only ever touches device RAM.
/// </remarks>
public static class Protocol
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>Feature report ID for config packets.</summary>
    public const byte ReportIdConfig = 100;

    /// <summary>Input report ID for monitor reports.</summary>
    public const byte ReportIdMonitor = 101;

    /// <summary>The only firmware config version this app writes to.</summary>
    public const byte ConfigVersion = 18;

    /// <summary>Config packets are always 33 bytes: report ID, version, command, 26 payload bytes, then a CRC32.</summary>
    public const int PacketLength = 33;

    /// <summary>Maximum payload size.</summary>
    public const int PayloadLength = 26;

    /// <summary>Vendor-defined usage page of both HID Remapper collections.</summary>
    public const ushort ConfigUsagePage = 0xFF00;

    /// <summary>Usage of the config collection (feature reports).</summary>
    public const ushort ConfigUsage = 0x0020;

    /// <summary>Usage of the monitor collection (input reports). Windows exposes it as a separate device.</summary>
    public const ushort MonitorUsage = 0x0021;

    /// <summary>Number of expression slots on the device.</summary>
    public const int ExpressionSlots = 8;

    /// <summary>Vendor-defined usage our expression reports the layer mask under. Can't collide with a real input usage.</summary>
    public const uint SentinelUsage = 0xFF00_0001;

    /// <summary>Report ID plus 7 packed 9-byte items.</summary>
    public const int MonitorReportLength = 1 + MonitorItems * MonitorItemLength;

    /// <summary>GET_CONFIG command.</summary>
    public const byte CmdGetConfig = 3;

    /// <summary>RESUME command. Required after appending, or the expression never evaluates.</summary>
    public const byte CmdResume = 11;

    /// <summary>APPEND_TO_EXPRESSION command.</summary>
    public const byte CmdAppendToExpression = 20;

    /// <summary>GET_EXPRESSION command.</summary>
    public const byte CmdGetExpression = 21;

    /// <summary>SET_MONITOR_ENABLED command.</summary>
    public const byte CmdSetMonitorEnabled = 22;

    internal const byte ExpressionElementCount = 3;

    private const int PayloadStart      = 3;
    private const int CrcStart          = 29;
    private const int MonitorItems      = 7;
    private const int MonitorItemLength = 9;

    /// <summary>
    /// Gets the encoded expression <c>layer_state 0xFF000001 monitor</c>.
    /// </summary>
    /// <remarks>
    /// Three elements, seven bytes. <c>layer_state</c> and <c>push_usage</c> each push one value,
    /// and <c>monitor</c> consumes two, so the firmware's validator accepts the balanced stack.
    /// </remarks>
    public static ReadOnlySpan<byte> ExpressionBytes => [20, 1, 0x01, 0x00, 0x00, 0xFF, 44];

    // =========================================================================
    // FRAMING
    // =========================================================================

    /// <summary>
    /// Builds a signed 33-byte config packet.
    /// </summary>
    /// <remarks>
    /// The CRC32 covers bytes 1 through 28 (everything between the report ID and the CRC field)
    /// and is written little-endian at byte 29.
    /// </remarks>
    /// <param name="command">The command byte.</param>
    /// <param name="payload">Up to 26 payload bytes.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload is longer than 26 bytes. Call sites use fixed sizes, so this is a bug, not bad input.</exception>
    public static byte[] BuildPacket(byte command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, PayloadLength);

        // Header And Payload
        var packet = new byte[PacketLength];
        packet[0]  = ReportIdConfig;
        packet[1]  = ConfigVersion;
        packet[2]  = command;
        payload.CopyTo(packet.AsSpan(PayloadStart));

        // Sign
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(CrcStart), CrcOf(packet));

        return packet;
    }

    /// <summary>
    /// Checks a packet's length and CRC.
    /// </summary>
    /// <remarks>
    /// The firmware's replies don't echo the command byte, so the CRC is the integrity check.
    /// </remarks>
    /// <param name="packet">The packet.</param>
    /// <returns><see langword="true"/> when the packet is at least 33 bytes and its CRC matches.</returns>
    public static bool VerifyCrc(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketLength)
        {
            return false;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(packet[CrcStart..PacketLength]) == CrcOf(packet);
    }

    private static uint CrcOf(ReadOnlySpan<byte> packet) => Crc32.HashToUInt32(packet[1..CrcStart]);

    // =========================================================================
    // COMMANDS
    // =========================================================================

    /// <summary>Builds GET_CONFIG.</summary>
    /// <remarks>The reply carries the config version in byte 1.</remarks>
    /// <returns>The packet.</returns>
    public static byte[] GetConfig() => BuildPacket(CmdGetConfig, []);

    /// <summary>Builds GET_EXPRESSION for one slot.</summary>
    /// <remarks>Payload is the slot as a u32, then the element offset (always 0) as a u32.</remarks>
    /// <param name="slot">The slot, 0 through 7.</param>
    /// <returns>The packet.</returns>
    public static byte[] GetExpression(byte slot)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, slot);
        return BuildPacket(CmdGetExpression, payload);
    }

    /// <summary>Builds APPEND_TO_EXPRESSION with our expression.</summary>
    /// <remarks>Payload is the slot, the element count (3), then the 7 expression bytes.</remarks>
    /// <param name="slot">The empty slot to append into.</param>
    /// <returns>The packet.</returns>
    public static byte[] AppendExpression(byte slot)
    {
        Span<byte> payload = stackalloc byte[2 + 7];
        payload[0]         = slot;
        payload[1]         = ExpressionElementCount;
        ExpressionBytes.CopyTo(payload[2..]);
        return BuildPacket(CmdAppendToExpression, payload);
    }

    /// <summary>Builds RESUME.</summary>
    /// <remarks>Required after appending: only RESUME marks the expression valid. It also resets the firmware's layer state to layer 0.</remarks>
    /// <returns>The packet.</returns>
    public static byte[] Resume() => BuildPacket(CmdResume, []);

    /// <summary>Builds SET_MONITOR_ENABLED.</summary>
    /// <remarks>Monitor mode streams input usages, including our sentinel, as input reports on the monitor collection.</remarks>
    /// <param name="enabled">Whether to enable Monitor mode.</param>
    /// <returns>The packet.</returns>
    public static byte[] SetMonitorEnabled(bool enabled) => BuildPacket(CmdSetMonitorEnabled, [enabled ? (byte)1 : (byte)0]);

    // =========================================================================
    // PARSING
    // =========================================================================

    /// <summary>Reads the config version from a GET_CONFIG reply.</summary>
    /// <remarks>A mismatch is surfaced, never guessed at: a wrong guess would write an expression the firmware reads as something else.</remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="version">The version.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid.</returns>
    public static bool TryParseConfigVersion(ReadOnlySpan<byte> packet, out byte version)
    {
        version = 0;
        if (!VerifyCrc(packet))
        {
            return false;
        }

        version = packet[1];
        return true;
    }

    /// <summary>Reads one slot from a GET_EXPRESSION reply.</summary>
    /// <remarks>Layout: report ID, element count, 27 element bytes, CRC.</remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="slot">The slot contents.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid.</returns>
    public static bool TryParseExpressionResponse(ReadOnlySpan<byte> packet, out SlotContents slot)
    {
        slot = new SlotContents(0, new byte[27]);
        if (!VerifyCrc(packet))
        {
            return false;
        }

        slot = new SlotContents(packet[1], packet[2..CrcStart].ToArray());
        return true;
    }

    /// <summary>Extracts the layer mask from a monitor report, if it carries our sentinel.</summary>
    /// <remarks>
    /// The firmware emits an item only when a value changes, so every sentinel item is a real layer switch.
    /// <c>layer_state</c> bypasses the ×1000 fixed-point convention, so the value is the raw mask in its low 8 bits.
    /// </remarks>
    /// <param name="report">The input report, including the report ID byte.</param>
    /// <param name="mask">The layer mask.</param>
    /// <returns><see langword="true"/> when the report is well formed and carries the sentinel.</returns>
    public static bool TryParseMonitorReport(ReadOnlySpan<byte> report, out LayerMask mask)
    {
        mask = default;
        if (report.Length < MonitorReportLength || report[0] != ReportIdMonitor)
        {
            return false;
        }

        // Scan The Seven Items
        for (var item = 0; item < MonitorItems; item++)
        {
            var offset = 1 + item * MonitorItemLength;
            var usage  = BinaryPrimitives.ReadUInt32LittleEndian(report[offset..]);
            if (usage != SentinelUsage)
            {
                continue;
            }

            var value = BinaryPrimitives.ReadInt32LittleEndian(report[(offset + 4)..]);
            mask      = new LayerMask(unchecked((byte)value));
            return true;
        }

        return false;
    }

    // =========================================================================
    // SLOT CHOICE
    // =========================================================================

    /// <summary>Decides which slot to use.</summary>
    /// <remarks>
    /// Reusing our own slot matters: without it, every app restart would use up another slot,
    /// and 8 restarts would fill the device.
    /// </remarks>
    /// <param name="slots">The slots in index order.</param>
    /// <returns>The choice.</returns>
    public static SlotChoice ChooseSlot(IReadOnlyList<SlotContents> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        // Ours Already Installed
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsOurs)
            {
                return new SlotChoice(SlotChoiceKind.Existing, (byte)i);
            }
        }

        // First Empty Slot
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].ElementCount == 0)
            {
                return new SlotChoice(SlotChoiceKind.Empty, (byte)i);
            }
        }

        return new SlotChoice(SlotChoiceKind.NoneFree, 0);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 4: HUD Rules, Timing, and Placement

**Files:**
- Create: `src/Layers.Core/Logic/HudRules.cs`, `src/Layers.Core/Logic/HudTiming.cs`, `src/Layers.Core/Logic/HudPlacement.cs`
- Test: `tests/Layers.Tests/HudRulesTests.cs`, `tests/Layers.Tests/HudTimingTests.cs`, `tests/Layers.Tests/HudPlacementTests.cs`

**Interfaces:**
- Consumes: `LayerMask`, `DeviceState`, `DeviceStatus`.
- Produces:
  - `readonly record struct HudSettings(bool HudEnabled, byte HudSuppressedLayers)` with `static HudSettings Default` (true, 0).
  - `static class HudRules` with `bool Allowed(HudSettings, LayerMask)`, `bool AllowedTransition(HudSettings, LayerMask, LayerMask)`, and `bool ShouldShow(HudSettings, DeviceState previous, DeviceState current)`.
  - `static class HudTiming` with `TimeSpan Hold` (550 ms), `Fade` (200 ms), `Tick` (16 ms), `double OpacityAt(TimeSpan)`, and `bool IsFinished(TimeSpan)`.
  - `static class HudPlacement` with constants `MinWidth=96`, `PadX=14`, `Height=48`, `Icon=20`, `IconTextGap=10`, `BottomGap=80`, `Corner=9`, plus `Windows.Graphics.SizeInt32 Size(double labelWidth, double scale)` and `Windows.Graphics.PointInt32 Place(Windows.Graphics.RectInt32 work, Windows.Graphics.SizeInt32 size, double scale)`.

- [ ] **Step 1: Write the failing tests** `tests/Layers.Tests/HudRulesTests.cs` (ports the 11 `settings.rs` tests plus the connect rules from `main.rs:220-229`)

```csharp
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HudRulesTests
{
    private static LayerMask L(byte bits) => new(bits);

    private static DeviceState Connected(byte bits) => new(DeviceStatus.Connected, L(bits));

    [TestMethod]
    public void Default_IsEnabledNothingSuppressed()
    {
        Assert.AreEqual(new HudSettings(true, 0), HudSettings.Default);
    }

    [TestMethod]
    public void Default_AllowsEveryLayer()
    {
        for (var layer = 0; layer < 8; layer++)
        {
            Assert.IsTrue(HudRules.Allowed(HudSettings.Default, L((byte)(1 << layer))));
        }
    }

    [TestMethod]
    public void Disabled_AllowsNothing()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(false, 0), L(0b10)));
    }

    [TestMethod]
    public void SuppressedLayer_IsSilent()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b10), L(0b10)));
    }

    [TestMethod]
    public void UnsuppressedLayer_StillShows()
    {
        Assert.IsTrue(HudRules.Allowed(new HudSettings(true, 0b10), L(0b100)));
    }

    [TestMethod]
    public void StackedWithSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b10), L(0b10_0010)));
    }

    [TestMethod]
    public void SuppressingLayerZero_SilencesBase()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b1), L(0b1)));
    }

    [TestMethod]
    public void Transition_IntoSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(true, 0b10), L(0b1), L(0b10)));
    }

    [TestMethod]
    public void Transition_OutOfSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(true, 0b10), L(0b10), L(0b1)));
    }

    [TestMethod]
    public void Transition_BetweenAllowed_Shows()
    {
        Assert.IsTrue(HudRules.AllowedTransition(new HudSettings(true, 0b10), L(0b1), L(0b100)));
    }

    [TestMethod]
    public void Transition_WhenDisabled_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(false, 0), L(0b1), L(0b100)));
    }

    [TestMethod]
    public void ShouldShow_OnConnectedLayerChange()
    {
        Assert.IsTrue(HudRules.ShouldShow(HudSettings.Default, Connected(0b1), Connected(0b100)));
    }

    [TestMethod]
    public void ShouldShow_NotWhenMaskUnchanged()
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, Connected(0b100), Connected(0b100)));
    }

    [TestMethod]
    [DataRow(DeviceStatus.Disconnected)]
    [DataRow(DeviceStatus.NoSlot)]
    [DataRow(DeviceStatus.VersionMismatch)]
    public void ShouldShow_NeverOnConnectOrReconnect(DeviceStatus before)
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, new DeviceState(before, L(0b100)), Connected(0b1)));
    }

    [TestMethod]
    public void ShouldShow_NotOnDisconnect()
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, Connected(0b100), new DeviceState(DeviceStatus.Disconnected, L(0b1))));
    }

    [TestMethod]
    public void ShouldShow_RespectsSuppressionBothWays()
    {
        var settings = new HudSettings(true, 0b10);

        Assert.IsFalse(HudRules.ShouldShow(settings, Connected(0b1), Connected(0b10)));
        Assert.IsFalse(HudRules.ShouldShow(settings, Connected(0b10), Connected(0b1)));
    }
}
```

`tests/Layers.Tests/HudTimingTests.cs`:

```csharp
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HudTimingTests
{
    private static double At(int ms) => HudTiming.OpacityAt(TimeSpan.FromMilliseconds(ms));

    [TestMethod]
    public void Constants_MatchRust()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(550), HudTiming.Hold);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), HudTiming.Fade);
        Assert.AreEqual(TimeSpan.FromMilliseconds(16), HudTiming.Tick);
    }

    [TestMethod]
    public void FullOpacity_ThroughHold()
    {
        Assert.AreEqual(1.0, At(0));
        Assert.AreEqual(1.0, At(549));
    }

    [TestMethod]
    public void FadeStarts_AtFullOpacity()
    {
        Assert.AreEqual(1.0, At(550), 1e-9);
    }

    [TestMethod]
    public void FadeMidpoint_IsHalf()
    {
        Assert.AreEqual(0.5, At(650), 1e-9);
    }

    [TestMethod]
    public void ReachesZero_AtEnd()
    {
        Assert.AreEqual(0.0, At(750));
        Assert.AreEqual(0.0, At(751));
    }

    [TestMethod]
    public void DecreasesMonotonically()
    {
        var previous = 1.0;
        for (var ms = 550; ms <= 750; ms += 10)
        {
            var current = At(ms);
            Assert.IsTrue(current <= previous);
            previous = current;
        }
    }

    [TestMethod]
    public void IsFinished_OnlyAfterHoldPlusFade()
    {
        Assert.IsFalse(HudTiming.IsFinished(TimeSpan.FromMilliseconds(749)));
        Assert.IsTrue(HudTiming.IsFinished(TimeSpan.FromMilliseconds(750)));
    }
}
```

`tests/Layers.Tests/HudPlacementTests.cs`:

```csharp
using Layers.Core.Logic;
using Windows.Graphics;

namespace Layers.Tests;

[TestClass]
public sealed class HudPlacementTests
{
    private static readonly RectInt32 Work = new(0, 0, 1920, 1040);

    [TestMethod]
    public void Size_HonorsMinimumWidth()
    {
        Assert.AreEqual(new SizeInt32(96, 48), HudPlacement.Size(10, 1.0));
    }

    [TestMethod]
    public void Size_GrowsForLongLabels()
    {
        // 14 + 20 + 10 + 120 + 14 = 178
        Assert.AreEqual(new SizeInt32(178, 48), HudPlacement.Size(120, 1.0));
    }

    [TestMethod]
    public void Size_ScalesWithDpi()
    {
        Assert.AreEqual(new SizeInt32(144, 72), HudPlacement.Size(10, 1.5));
    }

    [TestMethod]
    public void Place_CentersHorizontally()
    {
        var point = HudPlacement.Place(Work, new SizeInt32(100, 48), 1.0);

        Assert.AreEqual(910, point.X);
    }

    [TestMethod]
    public void Place_SitsBottomGapAboveWorkAreaBottom()
    {
        var point = HudPlacement.Place(Work, new SizeInt32(100, 48), 1.0);

        Assert.AreEqual(1040 - 80 - 48, point.Y);
    }

    [TestMethod]
    [DataRow(1.0, 80)]
    [DataRow(1.5, 120)]
    [DataRow(2.0, 160)]
    public void Place_ScalesBottomGap(double scale, int gap)
    {
        var point = HudPlacement.Place(Work, new SizeInt32(100, 48), scale);

        Assert.AreEqual(1040 - gap - 48, point.Y);
    }

    [TestMethod]
    public void Place_RespectsOffsetWorkArea()
    {
        // Taskbar docked left: work area starts at x=62
        var work  = new RectInt32(62, 0, 1858, 1080);
        var point = HudPlacement.Place(work, new SizeInt32(100, 48), 1.0);

        Assert.AreEqual(62 + (1858 - 100) / 2, point.X);
        Assert.AreEqual(1080 - 80 - 48, point.Y);
    }

    [TestMethod]
    public void Place_OnSecondaryOrigin()
    {
        var work  = new RectInt32(-1920, 200, 1920, 1040);
        var point = HudPlacement.Place(work, new SizeInt32(100, 48), 1.0);

        Assert.AreEqual(-1920 + 910, point.X);
        Assert.AreEqual(200 + 1040 - 80 - 48, point.Y);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors for the missing types.

- [ ] **Step 3: Implement `src/Layers.Core/Logic/HudRules.cs`**

```csharp
namespace Layers.Core.Logic;

/// <summary>
/// The user's HUD preferences.
/// </summary>
/// <remarks>
/// Persisted by <c>SettingsStore</c> as <c>HudEnabled</c> and <c>HudSuppressedLayers</c>.
/// </remarks>
/// <param name="HudEnabled">Master switch.</param>
/// <param name="HudSuppressedLayers">Bit <c>n</c> set means layer <c>n</c> is muted.</param>
public readonly record struct HudSettings(bool HudEnabled, byte HudSuppressedLayers)
{
    /// <summary>
    /// Gets the defaults: enabled, nothing muted.
    /// </summary>
    /// <remarks>
    /// Used when a registry value is missing or unreadable.
    /// </remarks>
    public static HudSettings Default => new(true, 0);
}

/// <summary>
/// Decides when the HUD appears.
/// </summary>
/// <remarks>
/// Ported from <c>settings.rs</c> and <c>main.rs</c> in Layers 1.0.3.
/// </remarks>
public static class HudRules
{
    /// <summary>
    /// Whether a HUD may show for this layer state.
    /// </summary>
    /// <remarks>
    /// Every active layer must be allowed, not just the highest. Holding a muted layer 1 while layer 5 is active
    /// gives "1, 5", and muting layer 1 has to mean silence there too.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="layers">The layer state.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public static bool Allowed(HudSettings settings, LayerMask layers) =>
        settings.HudEnabled && (layers.Bits & settings.HudSuppressedLayers) == 0;

    /// <summary>
    /// Whether a HUD may show for a move between two layer states.
    /// </summary>
    /// <remarks>
    /// Both ends must be allowed, so a muted layer is silent in both directions: releasing it doesn't announce
    /// the layer you land back on.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="from">The previous layers.</param>
    /// <param name="to">The new layers.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public static bool AllowedTransition(HudSettings settings, LayerMask from, LayerMask to) =>
        Allowed(settings, from) && Allowed(settings, to);

    /// <summary>
    /// Whether the HUD fires for this device state change.
    /// </summary>
    /// <remarks>
    /// The mask must change, the device must be Connected before and after (so connect and reconnect never fire it),
    /// and the transition must be allowed.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="previous">The state before the change.</param>
    /// <param name="current">The state after the change.</param>
    /// <returns><see langword="true"/> when the HUD should show.</returns>
    public static bool ShouldShow(HudSettings settings, DeviceState previous, DeviceState current) =>
        previous.Status == DeviceStatus.Connected
        && current.Status == DeviceStatus.Connected
        && previous.Layers != current.Layers
        && AllowedTransition(settings, previous.Layers, current.Layers);
}
```

- [ ] **Step 4: Implement `src/Layers.Core/Logic/HudTiming.cs`**

```csharp
namespace Layers.Core.Logic;

/// <summary>
/// The HUD's hold-then-fade curve.
/// </summary>
/// <remarks>
/// Full opacity for 550 ms, then a linear fade to zero over 200 ms, stepped every 16 ms. Values match Layers 1.0.3.
/// </remarks>
public static class HudTiming
{
    /// <summary>Gets how long the HUD stays fully opaque.</summary>
    public static TimeSpan Hold { get; } = TimeSpan.FromMilliseconds(550);

    /// <summary>Gets how long the fade lasts.</summary>
    public static TimeSpan Fade { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets the fade timer interval.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Gets the opacity at a point in time.
    /// </summary>
    /// <remarks>
    /// Returns 1 during the hold, then ramps linearly to 0.
    /// </remarks>
    /// <param name="elapsed">Time since the HUD was shown.</param>
    /// <returns>Opacity from 0 to 1.</returns>
    public static double OpacityAt(TimeSpan elapsed)
    {
        if (elapsed < Hold)
        {
            return 1.0;
        }

        var fading = elapsed - Hold;
        if (fading >= Fade)
        {
            return 0.0;
        }

        return (Fade - fading) / Fade;
    }

    /// <summary>
    /// Whether the fade has completed.
    /// </summary>
    /// <remarks>
    /// The HUD hides once this is true.
    /// </remarks>
    /// <param name="elapsed">Time since the HUD was shown.</param>
    /// <returns><see langword="true"/> at or after hold plus fade.</returns>
    public static bool IsFinished(TimeSpan elapsed) => elapsed >= Hold + Fade;
}
```

- [ ] **Step 5: Implement `src/Layers.Core/Logic/HudPlacement.cs`**

```csharp
using Windows.Graphics;

namespace Layers.Core.Logic;

/// <summary>
/// HUD size and position math.
/// </summary>
/// <remarks>
/// Layout values are logical pixels at 96 DPI, scaled by the primary monitor's scale. The HUD sits bottom-center
/// on the primary monitor's work area, 80 px above its bottom, and doesn't follow the cursor's monitor.
/// </remarks>
public static class HudPlacement
{
    /// <summary>Minimum panel width.</summary>
    public const double MinWidth = 96;

    /// <summary>Horizontal padding on each side.</summary>
    public const double PadX = 14;

    /// <summary>Panel height.</summary>
    public const double Height = 48;

    /// <summary>Glyph size.</summary>
    public const double Icon = 20;

    /// <summary>Gap between glyph and label.</summary>
    public const double IconTextGap = 10;

    /// <summary>Distance from the work area's bottom to the panel's bottom.</summary>
    public const double BottomGap = 80;

    /// <summary>Corner radius.</summary>
    public const double Corner = 9;

    /// <summary>
    /// Gets the panel size in physical pixels.
    /// </summary>
    /// <remarks>
    /// The panel fits its content (padding, glyph, gap, label, padding) but is never narrower than 96.
    /// </remarks>
    /// <param name="labelWidth">Measured label width in logical pixels.</param>
    /// <param name="scale">DPI scale, for example 1.5 at 144 DPI.</param>
    /// <returns>The size.</returns>
    public static SizeInt32 Size(double labelWidth, double scale)
    {
        var width = Math.Max(MinWidth, PadX + Icon + IconTextGap + labelWidth + PadX);

        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(Height * scale));
    }

    /// <summary>
    /// Gets the panel's top-left corner in physical pixels.
    /// </summary>
    /// <remarks>
    /// Centered horizontally on the work area, with the bottom edge 80 logical pixels above the work area's bottom.
    /// </remarks>
    /// <param name="work">The primary monitor's work area.</param>
    /// <param name="size">The panel size from <see cref="Size"/>.</param>
    /// <param name="scale">DPI scale.</param>
    /// <returns>The position.</returns>
    public static PointInt32 Place(RectInt32 work, SizeInt32 size, double scale)
    {
        var gap = (int)Math.Round(BottomGap * scale);
        var x   = work.X + (work.Width - size.Width) / 2;
        var y   = work.Y + work.Height - gap - size.Height;

        return new PointInt32(x, y);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 5: Icon Math

**Files:**
- Create: `src/Layers.Core/Logic/IconMath.cs`
- Test: `tests/Layers.Tests/IconMathTests.cs`

**Interfaces:**
- Produces:
  - `sealed class AlphaBuffer(int Width, int Height)` with `byte[] Pixels`, indexer `this[int x, int y]`, and `(int MinX, int MinY, int MaxX, int MaxY)? InkBounds()`
  - `static class IconMath` with `int IconSize(uint dpi)`, `AlphaBuffer Downsample(AlphaBuffer, int factor)`, `AlphaBuffer CenterInk(AlphaBuffer)`, and `byte[] ToPremultipliedBgra(AlphaBuffer, byte r, byte g, byte b)`

- [ ] **Step 1: Write the failing tests** `tests/Layers.Tests/IconMathTests.cs` (ports the 13 `compose.rs` tests, plus size)

```csharp
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class IconMathTests
{
    private static AlphaBuffer Filled(int w, int h, byte value)
    {
        var buffer = new AlphaBuffer(w, h);
        Array.Fill(buffer.Pixels, value);
        return buffer;
    }

    // =========================================================================
    // SIZE
    // =========================================================================

    [TestMethod]
    [DataRow(96u, 16)]
    [DataRow(120u, 20)]
    [DataRow(144u, 24)]
    [DataRow(192u, 32)]
    [DataRow(168u, 28)]
    [DataRow(108u, 20)]
    public void IconSize_RoundsUpToMultipleOfFour(uint dpi, int expected)
    {
        Assert.AreEqual(expected, IconMath.IconSize(dpi));
    }

    // =========================================================================
    // DOWNSAMPLE
    // =========================================================================

    [TestMethod]
    public void Downsample_AveragesBlocks()
    {
        var src = new AlphaBuffer(2, 2);
        src.Pixels[0] = 255;
        src.Pixels[1] = 255;

        var result = IconMath.Downsample(src, 2);

        Assert.AreEqual(1, result.Width);
        Assert.AreEqual((byte)127, result.Pixels[0]);
    }

    [TestMethod]
    public void Downsample_FullStaysFull()
    {
        Assert.IsTrue(IconMath.Downsample(Filled(8, 8, 255), 4).Pixels.All(p => p == 255));
    }

    [TestMethod]
    public void Downsample_EmptyStaysEmpty()
    {
        Assert.IsTrue(IconMath.Downsample(new AlphaBuffer(8, 8), 4).Pixels.All(p => p == 0));
    }

    [TestMethod]
    public void Downsample_KeepsBlocksSeparate()
    {
        var src = new AlphaBuffer(4, 2);
        src[2, 0] = 255;
        src[3, 0] = 255;
        src[2, 1] = 255;
        src[3, 1] = 255;

        var result = IconMath.Downsample(src, 2);

        CollectionAssert.AreEqual(new byte[] { 0, 255 }, result.Pixels);
    }

    [TestMethod]
    public void Downsample_FactorOneIsIdentity()
    {
        var src = new AlphaBuffer(3, 3);
        src[1, 1] = 42;

        CollectionAssert.AreEqual(src.Pixels, IconMath.Downsample(src, 1).Pixels);
    }

    [TestMethod]
    public void Downsample_RejectsNonDividingFactor()
    {
        Assert.ThrowsExactly<ArgumentException>(() => IconMath.Downsample(new AlphaBuffer(5, 4), 2));
    }

    [TestMethod]
    public void Downsample_RejectsZeroFactor()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => IconMath.Downsample(new AlphaBuffer(4, 4), 0));
    }

    // =========================================================================
    // PREMULTIPLY
    // =========================================================================

    [TestMethod]
    public void Premultiply_FullCoverageKeepsColor()
    {
        var bgra = IconMath.ToPremultipliedBgra(Filled(1, 1, 255), 0x10, 0x20, 0x30);

        CollectionAssert.AreEqual(new byte[] { 0x30, 0x20, 0x10, 255 }, bgra);
    }

    [TestMethod]
    public void Premultiply_ZeroCoverageIsTransparentBlack()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, IconMath.ToPremultipliedBgra(new AlphaBuffer(1, 1), 255, 255, 255));
    }

    [TestMethod]
    public void Premultiply_HalfCoverageHalvesColor()
    {
        var bgra = IconMath.ToPremultipliedBgra(Filled(1, 1, 128), 255, 255, 255);

        CollectionAssert.AreEqual(new byte[] { 128, 128, 128, 128 }, bgra);
    }

    // =========================================================================
    // CENTER INK
    // =========================================================================

    [TestMethod]
    public void CenterInk_MovesLowInkUp()
    {
        var src = new AlphaBuffer(8, 8);
        src[3, 6] = 255;
        src[4, 7] = 255;

        var bounds = IconMath.CenterInk(src).InkBounds()!.Value;

        Assert.AreEqual(3, bounds.MinY);
        Assert.AreEqual(4, bounds.MaxY);
    }

    [TestMethod]
    public void CenterInk_MovesLeftInkRight()
    {
        var src = new AlphaBuffer(8, 8);
        src[0, 3] = 255;
        src[1, 4] = 255;

        var bounds = IconMath.CenterInk(src).InkBounds()!.Value;

        Assert.AreEqual(3, bounds.MinX);
        Assert.AreEqual(4, bounds.MaxX);
    }

    [TestMethod]
    public void CenterInk_EmptyUnchanged()
    {
        var src = new AlphaBuffer(8, 8);

        CollectionAssert.AreEqual(src.Pixels, IconMath.CenterInk(src).Pixels);
    }

    [TestMethod]
    public void CenterInk_AlreadyCenteredUnchanged()
    {
        var src = new AlphaBuffer(8, 8);
        src[3, 3] = 255;
        src[4, 4] = 255;

        CollectionAssert.AreEqual(src.Pixels, IconMath.CenterInk(src).Pixels);
    }

    [TestMethod]
    public void CenterInk_PreservesCoverageValues()
    {
        var src = new AlphaBuffer(8, 8);
        src[0, 0] = 17;

        Assert.IsTrue(IconMath.CenterInk(src).Pixels.Contains((byte)17));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors.

- [ ] **Step 3: Implement `src/Layers.Core/Logic/IconMath.cs`**

```csharp
namespace Layers.Core.Logic;

/// <summary>
/// A single-channel coverage buffer, one byte per pixel, row major.
/// </summary>
/// <remarks>
/// The tray icon is monochrome, so it's rasterized as coverage and tinted at the end.
/// </remarks>
/// <param name="width">Width in pixels.</param>
/// <param name="height">Height in pixels.</param>
public sealed class AlphaBuffer(int width, int height)
{
    /// <summary>Gets the width.</summary>
    public int Width { get; } = width;

    /// <summary>Gets the height.</summary>
    public int Height { get; } = height;

    /// <summary>Gets the coverage values, row major.</summary>
    public byte[] Pixels { get; } = new byte[width * height];

    /// <summary>
    /// Gets or sets one pixel.
    /// </summary>
    /// <remarks>
    /// Convenience for tests and the rasterizer.
    /// </remarks>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>
    /// Gets the bounding box of non-zero coverage.
    /// </summary>
    /// <remarks>
    /// Inclusive bounds, or <see langword="null"/> for an empty buffer.
    /// </remarks>
    /// <returns>The bounds.</returns>
    public (int MinX, int MinY, int MaxX, int MaxY)? InkBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (this[x, y] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}

/// <summary>
/// Pixel math for the tray icon.
/// </summary>
/// <remarks>
/// Ported from <c>compose.rs</c> in Layers 1.0.3.
/// </remarks>
public static class IconMath
{
    /// <summary>
    /// Gets the tray icon size for a DPI.
    /// </summary>
    /// <remarks>
    /// <c>16 * dpi / 96</c> (integer division), rounded up to a multiple of 4 so the 4× supersample divides evenly.
    /// </remarks>
    /// <param name="dpi">The taskbar's DPI.</param>
    /// <returns>The size in pixels.</returns>
    public static int IconSize(uint dpi)
    {
        var size = (int)(16 * dpi / 96);
        return (size + 3) / 4 * 4;
    }

    /// <summary>
    /// Box-filter downsample by an integer factor.
    /// </summary>
    /// <remarks>
    /// Rendering at 4× and reducing gives the digit clean edges at 16 pixels.
    /// </remarks>
    /// <param name="source">The large buffer.</param>
    /// <param name="factor">The reduction factor.</param>
    /// <returns>The reduced buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="factor"/> doesn't divide both dimensions.</exception>
    public static AlphaBuffer Downsample(AlphaBuffer source, int factor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        if (source.Width % factor != 0 || source.Height % factor != 0)
        {
            throw new ArgumentException("factor must divide both dimensions", nameof(factor));
        }

        // Average Each Block
        var result = new AlphaBuffer(source.Width / factor, source.Height / factor);
        var count  = factor * factor;
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += source[x * factor + dx, y * factor + dy];
                    }
                }

                result[x, y] = (byte)(sum / count);
            }
        }

        return result;
    }

    /// <summary>
    /// Shifts the buffer so its ink sits centered.
    /// </summary>
    /// <remarks>
    /// Text layout centers the line box (ascent plus descent), not the ink, so a "centered" digit renders low.
    /// Measuring what was actually rasterized and shifting it works for any font.
    /// An empty buffer is returned unchanged.
    /// </remarks>
    /// <param name="source">The buffer.</param>
    /// <returns>A centered copy.</returns>
    public static AlphaBuffer CenterInk(AlphaBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Measure
        var result = new AlphaBuffer(source.Width, source.Height);
        var bounds = source.InkBounds();
        if (bounds is null)
        {
            source.Pixels.CopyTo(result.Pixels, 0);
            return result;
        }

        var (minX, minY, maxX, maxY) = bounds.Value;

        // Shift
        var dx = (source.Width - 1 - maxX - minX) / 2;
        var dy = (source.Height - 1 - maxY - minY) / 2;
        for (var y = 0; y < source.Height; y++)
        {
            var sy = y - dy;
            if (sy < 0 || sy >= source.Height)
            {
                continue;
            }

            for (var x = 0; x < source.Width; x++)
            {
                var sx = x - dx;
                if (sx < 0 || sx >= source.Width)
                {
                    continue;
                }

                result[x, y] = source[sx, sy];
            }
        }

        return result;
    }

    /// <summary>
    /// Expands coverage into premultiplied BGRA.
    /// </summary>
    /// <remarks>
    /// This is the format <c>CreateDIBSection</c> and <c>CreateIconIndirect</c> expect for 32-bit icons.
    /// </remarks>
    /// <param name="coverage">The coverage buffer.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>4 bytes per pixel, B G R A.</returns>
    public static byte[] ToPremultipliedBgra(AlphaBuffer coverage, byte r, byte g, byte b)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        var output = new byte[coverage.Pixels.Length * 4];
        for (var i = 0; i < coverage.Pixels.Length; i++)
        {
            var alpha         = coverage.Pixels[i];
            output[i * 4]     = (byte)(b * alpha / 255);
            output[i * 4 + 1] = (byte)(g * alpha / 255);
            output[i * 4 + 2] = (byte)(r * alpha / 255);
            output[i * 4 + 3] = alpha;
        }

        return output;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 6: Settings Store, Taskbar Theme, and Start at Sign-In

**Files:**
- Create: `src/Layers.Core/Services/SettingsStore.cs`, `src/Layers.Core/Services/TaskbarTheme.cs`, `src/Layers.Core/Services/StartupRegistration.cs`
- Test: `tests/Layers.Tests/TempRegistryKey.cs`, `tests/Layers.Tests/SettingsStoreTests.cs`, `tests/Layers.Tests/TaskbarThemeTests.cs`, `tests/Layers.Tests/StartupRegistrationTests.cs`

**Interfaces:**
- Consumes: `HudSettings`.
- Produces:
  - `sealed class SettingsStore(string keyPath = SettingsStore.DefaultKeyPath)` with `const string DefaultKeyPath = @"Software\Layers"`, `HudSettings Load()`, and `void Save(HudSettings)`.
  - `static class TaskbarTheme` with `const string PersonalizeKeyPath` and `bool IsLight(string keyPath = PersonalizeKeyPath)`.
  - `sealed record StartupState(bool IsEnabled, bool IsControllable, string? Note)`.
  - `interface IStartupRegistration` with `Task<StartupState> GetStateAsync()` and `Task<StartupState> SetEnabledAsync(bool enabled)`.
  - `sealed class RunKeyStartup(string exePath, string runKeyPath = RunKeyStartup.DefaultRunKeyPath)` with `const string ValueName = "Layers"`.
  - `sealed class PackagedStartupTask` with `const string TaskId = "LayersStartup"` and `internal static StartupState Map(Windows.ApplicationModel.StartupTaskState)`.

- [ ] **Step 1: Write the test helper** `tests/Layers.Tests/TempRegistryKey.cs`

```csharp
using Microsoft.Win32;

namespace Layers.Tests;

/// A throwaway HKCU key, deleted on dispose, so registry tests never touch real settings.
internal sealed class TempRegistryKey : IDisposable
{
    public string Path { get; } = $@"Software\LayersTests\{Guid.NewGuid():N}";

    public void Set(string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue(name, value, kind);
    }

    public object? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Layers.Tests/SettingsStoreTests.cs`:

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;
using Microsoft.Win32;

namespace Layers.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void DefaultKeyPath_MatchesRust()
    {
        Assert.AreEqual(@"Software\Layers", SettingsStore.DefaultKeyPath);
    }

    [TestMethod]
    public void Load_MissingKeyGivesDefaults()
    {
        using var temp = new TempRegistryKey();

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void SaveThenLoad_RoundTrips()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);
        var settings   = new HudSettings(false, 0b1010_0101);

        store.Save(settings);

        Assert.AreEqual(settings, store.Load());
        Assert.AreEqual(0, temp.Get("HudEnabled"));
        Assert.AreEqual(0b1010_0101, temp.Get("HudSuppressedLayers"));
    }

    [TestMethod]
    public void Load_MasksToEightBits()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudSuppressedLayers", 0x1_0102, RegistryValueKind.DWord);

        Assert.AreEqual((byte)0x02, new SettingsStore(temp.Path).Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void Load_WrongTypeFallsBackToDefault()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudEnabled", "0", RegistryValueKind.String);
        temp.Set("HudSuppressedLayers", new byte[] { 1 }, RegistryValueKind.Binary);

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void Load_IgnoresStaleSeenLayers()
    {
        using var temp = new TempRegistryKey();
        temp.Set("SeenLayers", 0xFF, RegistryValueKind.DWord);
        temp.Set("HudEnabled", 1, RegistryValueKind.DWord);

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void Save_FailureDoesNotThrow()
    {
        // A path with an embedded null can't be created
        var store = new SettingsStore("Software\\Layers\0Bad");

        store.Save(HudSettings.Default);
    }
}
```

`tests/Layers.Tests/TaskbarThemeTests.cs`:

```csharp
using Layers.Core.Services;
using Microsoft.Win32;

namespace Layers.Tests;

[TestClass]
public sealed class TaskbarThemeTests
{
    [TestMethod]
    public void Missing_MeansDarkTaskbar()
    {
        using var temp = new TempRegistryKey();

        Assert.IsFalse(TaskbarTheme.IsLight(temp.Path));
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(0, false)]
    public void ReadsSystemUsesLightTheme(int value, bool expected)
    {
        using var temp = new TempRegistryKey();
        temp.Set("SystemUsesLightTheme", value, RegistryValueKind.DWord);

        Assert.AreEqual(expected, TaskbarTheme.IsLight(temp.Path));
    }

    [TestMethod]
    public void PersonalizeKeyPath_IsTheWindowsKey()
    {
        Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", TaskbarTheme.PersonalizeKeyPath);
    }
}
```

`tests/Layers.Tests/StartupRegistrationTests.cs`:

```csharp
using Layers.Core.Services;
using Windows.ApplicationModel;

namespace Layers.Tests;

[TestClass]
public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\Test\AppData\Local\Programs\Layers\Layers.exe";

    [TestMethod]
    public async Task RunKey_DisabledWhenMissing()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).GetStateAsync();

        Assert.AreEqual(new StartupState(false, true, null), state);
    }

    [TestMethod]
    public async Task RunKey_EnableWritesQuotedPath()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).SetEnabledAsync(true);

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual($"\"{Exe}\"", temp.Get("Layers"));
    }

    [TestMethod]
    public async Task RunKey_DisableRemovesValue()
    {
        using var temp = new TempRegistryKey();
        var startup    = new RunKeyStartup(Exe, temp.Path);
        await startup.SetEnabledAsync(true);

        var state = await startup.SetEnabledAsync(false);

        Assert.IsFalse(state.IsEnabled);
        Assert.IsNull(temp.Get("Layers"));
    }

    [TestMethod]
    public async Task RunKey_DisableWhenMissingIsFine()
    {
        using var temp = new TempRegistryKey();

        var state = await new RunKeyStartup(Exe, temp.Path).SetEnabledAsync(false);

        Assert.IsFalse(state.IsEnabled);
    }

    [TestMethod]
    public void RunKey_DefaultPathIsCurrentUserRun()
    {
        Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKeyStartup.DefaultRunKeyPath);
    }

    [TestMethod]
    public void Packaged_MapsEveryState()
    {
        Assert.AreEqual(new StartupState(true, true, null), PackagedStartupTask.Map(StartupTaskState.Enabled));
        Assert.AreEqual(new StartupState(false, true, null), PackagedStartupTask.Map(StartupTaskState.Disabled));
        Assert.AreEqual(new StartupState(false, false, "Turned off in Settings › Apps › Startup."), PackagedStartupTask.Map(StartupTaskState.DisabledByUser));
        Assert.AreEqual(new StartupState(false, false, "Turned off by your organization."), PackagedStartupTask.Map(StartupTaskState.DisabledByPolicy));
        Assert.AreEqual(new StartupState(true, false, "Turned on by your organization."), PackagedStartupTask.Map(StartupTaskState.EnabledByPolicy));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors.

- [ ] **Step 4: Implement `src/Layers.Core/Services/SettingsStore.cs`**

```csharp
using System.Diagnostics;
using System.Security;
using Layers.Core.Logic;
using Microsoft.Win32;

namespace Layers.Core.Services;

/// <summary>
/// Loads and saves HUD settings in the registry.
/// </summary>
/// <remarks>
/// Uses <c>HKCU\Software\Layers</c> with the same DWORD value names as Layers 1.0.3, so existing users keep their settings.
/// Values are validated on load: a missing or wrong-typed value uses the default, and the mute mask keeps only its low 8 bits.
/// A stale <c>SeenLayers</c> value from older builds is ignored. Under MSIX, HKCU writes are virtualized per package,
/// so the MSI and MSIX builds keep separate settings.
/// </remarks>
/// <param name="keyPath">The HKCU-relative key. Tests pass a throwaway key.</param>
public sealed class SettingsStore(string keyPath = SettingsStore.DefaultKeyPath)
{
    /// <summary>The production key path.</summary>
    public const string DefaultKeyPath = @"Software\Layers";

    private const string HudEnabledName          = "HudEnabled";
    private const string HudSuppressedLayersName = "HudSuppressedLayers";

    /// <summary>
    /// Loads the settings.
    /// </summary>
    /// <remarks>
    /// Never throws. Unreadable values fall back to <see cref="HudSettings.Default"/>.
    /// </remarks>
    /// <returns>The settings.</returns>
    public HudSettings Load()
    {
        var defaults = HudSettings.Default;

        try
        {
            // Open Or Use Defaults
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null)
            {
                return defaults;
            }

            // Read And Validate
            var enabled    = key.GetValue(HudEnabledName) is int e ? e != 0 : defaults.HudEnabled;
            var suppressed = key.GetValue(HudSuppressedLayersName) is int s ? (byte)(s & 0xFF) : defaults.HudSuppressedLayers;

            return new HudSettings(enabled, suppressed);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("settings load failed: {0}", ex.Message);
            return defaults;
        }
    }

    /// <summary>
    /// Saves the settings.
    /// </summary>
    /// <remarks>
    /// Best effort: a failure is traced and never breaks the running app.
    /// </remarks>
    /// <param name="settings">The settings.</param>
    public void Save(HudSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(HudEnabledName, settings.HudEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(HudSuppressedLayersName, (int)settings.HudSuppressedLayers, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("settings save failed: {0}", ex.Message);
        }
    }
}
```

- [ ] **Step 5: Implement `src/Layers.Core/Services/TaskbarTheme.cs`**

```csharp
using Microsoft.Win32;

namespace Layers.Core.Services;

/// <summary>
/// Reads whether the taskbar uses the light theme.
/// </summary>
/// <remarks>
/// The tray icon follows the taskbar theme (<c>SystemUsesLightTheme</c>), not the app theme. There's no WinRT API for
/// the taskbar theme, so this reads the registry. A missing value means a dark taskbar, matching Windows' default.
/// </remarks>
public static class TaskbarTheme
{
    /// <summary>The Windows personalization key.</summary>
    public const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Whether the taskbar is light.
    /// </summary>
    /// <remarks>
    /// Read fresh on every tray refresh, which happens on <c>WM_SETTINGCHANGE</c>.
    /// </remarks>
    /// <param name="keyPath">The HKCU-relative key. Tests pass a throwaway key.</param>
    /// <returns><see langword="true"/> for a light taskbar.</returns>
    public static bool IsLight(string keyPath = PersonalizeKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }
}
```

- [ ] **Step 6: Implement `src/Layers.Core/Services/StartupRegistration.cs`**

```csharp
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Layers.Core.Services;

/// <summary>
/// Whether the app starts at sign-in, and whether the user can change that here.
/// </summary>
/// <remarks>
/// <see cref="Note"/> explains why the switch is locked, when it is.
/// </remarks>
/// <param name="IsEnabled">Starts at sign-in.</param>
/// <param name="IsControllable">The Settings switch can change it.</param>
/// <param name="Note">Shown under a locked switch. Verbatim user-facing text.</param>
public sealed record StartupState(bool IsEnabled, bool IsControllable, string? Note);

/// <summary>
/// Controls start at sign-in.
/// </summary>
/// <remarks>
/// <see cref="RunKeyStartup"/> for the MSI build and <see cref="PackagedStartupTask"/> for the MSIX build.
/// The app picks one at launch with <see cref="AppPackaging.IsPackaged"/>.
/// </remarks>
public interface IStartupRegistration
{
    /// <summary>Reads the current state.</summary>
    /// <remarks>Called when the Settings window opens.</remarks>
    /// <returns>The state.</returns>
    Task<StartupState> GetStateAsync();

    /// <summary>Turns start at sign-in on or off.</summary>
    /// <remarks>Returns the resulting state, which may differ from the request (for example when Windows refuses).</remarks>
    /// <param name="enabled">The requested state.</param>
    /// <returns>The resulting state.</returns>
    Task<StartupState> SetEnabledAsync(bool enabled);
}

/// <summary>
/// Start at sign-in through the HKCU Run key.
/// </summary>
/// <remarks>
/// The value is the quoted full exe path, so a path with spaces can't be misread. The MSI writes the same value at
/// install and removes it at uninstall.
/// </remarks>
/// <param name="exePath">Full path of Layers.exe.</param>
/// <param name="runKeyPath">The HKCU-relative Run key. Tests pass a throwaway key.</param>
public sealed class RunKeyStartup(string exePath, string runKeyPath = RunKeyStartup.DefaultRunKeyPath) : IStartupRegistration
{
    /// <summary>The per-user Run key.</summary>
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value name.</summary>
    public const string ValueName = "Layers";

    /// <inheritdoc/>
    public Task<StartupState> GetStateAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return Task.FromResult(new StartupState(key?.GetValue(ValueName) is string, true, null));
    }

    /// <inheritdoc/>
    public async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("run key update failed: {0}", ex.Message);
        }

        return await GetStateAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Start at sign-in through the MSIX <c>StartupTask</c> extension.
/// </summary>
/// <remarks>
/// The task is declared in <c>Package.appxmanifest</c> with ID <c>LayersStartup</c>, enabled by default.
/// Windows can lock it: if the user turned it off in Settings, only Settings can turn it back on.
/// </remarks>
public sealed class PackagedStartupTask : IStartupRegistration
{
    /// <summary>The manifest task ID.</summary>
    public const string TaskId = "LayersStartup";

    /// <inheritdoc/>
    public async Task<StartupState> GetStateAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return Map(task.State);
    }

    /// <inheritdoc/>
    public async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (enabled)
        {
            return Map(await task.RequestEnableAsync());
        }

        task.Disable();
        return Map(task.State);
    }

    /// <summary>Maps a Windows task state to a <see cref="StartupState"/>.</summary>
    /// <remarks>Notes are verbatim user-facing text.</remarks>
    /// <param name="state">The Windows state.</param>
    /// <returns>The mapped state.</returns>
    internal static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled          => new StartupState(true, true, null),
        StartupTaskState.Disabled         => new StartupState(false, true, null),
        StartupTaskState.DisabledByUser   => new StartupState(false, false, "Turned off in Settings › Apps › Startup."),
        StartupTaskState.DisabledByPolicy => new StartupState(false, false, "Turned off by your organization."),
        StartupTaskState.EnabledByPolicy  => new StartupState(true, false, "Turned on by your organization."),
        _                                 => new StartupState(false, false, null),
    };
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 7: Device Service

**Files:**
- Create: `src/Layers.Core/Services/Hid.cs`, `src/Layers.Core/Services/DeviceService.cs`
- Test: `tests/Layers.Tests/Fakes/FakeFirmware.cs`, `tests/Layers.Tests/Fakes/Eventually.cs`, `tests/Layers.Tests/DeviceServiceTests.cs`

**Interfaces:**
- Consumes: `Protocol`, `LayerMask`, `DeviceState`, `DeviceStatus`, `SlotChoiceKind`, `SlotContents`, `TryParse<T>`.
- Produces:
  - `interface IHidChannel : IDisposable` with `Task SendFeatureAsync(byte[] report, CancellationToken)`, `Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken)`, and `event EventHandler<byte[]>? InputReport`.
  - `sealed class HidDevicePair(IHidChannel config, IHidChannel monitor) : IDisposable` with `Config` and `Monitor`.
  - `interface IHidDeviceSource` with `event EventHandler<string>? Arrived`, `event EventHandler<string>? Departed`, `void Start()`, `void Stop()`, and `Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken)`.
  - `sealed class DeviceProtocolException : Exception`.
  - `sealed class DeviceService(IHidDeviceSource source, TimeProvider time) : IAsyncDisposable` with `event EventHandler<DeviceState>? StateChanged`, `DeviceState State`, `void Start()`, `ValueTask DisposeAsync()`, and the constants `ReconnectDelay`, `VerifyInterval`, `MissesBeforeReinstall`, `ReadAttempts`.

- [ ] **Step 1: Write the fakes** `tests/Layers.Tests/Fakes/FakeFirmware.cs`

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Tests.Fakes;

/// Simulates the config collection of HID Remapper firmware: 8 slots, GET_CONFIG, GET/APPEND expression, RESUME, monitor flag.
internal sealed class FakeConfigChannel : IHidChannel
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _sent = [];
    private byte[]? _pending;

    public byte Version { get; set; } = Protocol.ConfigVersion;
    public (byte Count, byte[] Bytes)[] Slots { get; } = Enumerable.Range(0, 8).Select(_ => ((byte)0, Array.Empty<byte>())).ToArray();
    public int ShortReplies { get; set; }
    public bool CorruptNextReply { get; set; }
    public Exception? SendFailure { get; set; }
    public int SendAttempts { get; private set; }
    public bool MonitorEnabled { get; private set; }
    public bool Disposed { get; set; }

    public event EventHandler<byte[]>? InputReport { add { } remove { } }

    public IReadOnlyList<byte[]> Sent { get { lock (_gate) { return _sent.ToList(); } } }
    public IReadOnlyList<byte> Commands => Sent.Select(packet => packet[2]).ToList();
    public int Count(byte command) => Commands.Count(c => c == command);

    public void FillAllSlots()
    {
        for (var i = 0; i < 8; i++)
        {
            Slots[i] = (2, [20, 44]);
        }
    }

    public void WipeSlots()
    {
        for (var i = 0; i < 8; i++)
        {
            Slots[i] = (0, []);
        }
    }

    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SendAttempts++;
            if (SendFailure is not null)
            {
                throw SendFailure;
            }

            _sent.Add(report.ToArray());
            switch (report[2])
            {
                case Protocol.CmdGetConfig:
                    _pending = Replies.Config(Version);
                    break;
                case Protocol.CmdGetExpression:
                    var slot = Slots[report[3]];
                    _pending = Replies.Expression(slot.Count, slot.Bytes);
                    break;
                case Protocol.CmdAppendToExpression:
                    Slots[report[3]] = (report[4], report[5..12]);
                    _pending = null;
                    break;
                case Protocol.CmdSetMonitorEnabled:
                    MonitorEnabled = report[3] == 1;
                    _pending = null;
                    break;
                default:
                    _pending = null;
                    break;
            }
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (ShortReplies > 0)
            {
                ShortReplies--;
                return Task.FromResult(new byte[] { Protocol.ReportIdConfig, 0, 0 });
            }

            // No Pending Reply Is Zeros With A Bad CRC
            var reply = (_pending ?? new byte[Protocol.PacketLength]).ToArray();
            if (CorruptNextReply)
            {
                CorruptNextReply = false;
                reply[5] ^= 0xFF;
            }

            return Task.FromResult(reply);
        }
    }

    public void Dispose() => Disposed = true;
}

/// Simulates the monitor collection: tests push input reports.
internal sealed class FakeMonitorChannel : IHidChannel
{
    public bool Disposed { get; set; }

    public event EventHandler<byte[]>? InputReport;

    public void Raise(byte[] report) => InputReport?.Invoke(this, report);

    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void Dispose() => Disposed = true;
}

/// Simulates DeviceWatcher plus opening. The same channel objects are reused across opens, like a device keeping its RAM.
internal sealed class FakeHidDeviceSource : IHidDeviceSource
{
    public FakeConfigChannel Config { get; } = new();
    public FakeMonitorChannel Monitor { get; } = new();
    public int Opens { get; private set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public event EventHandler<string>? Arrived;
    public event EventHandler<string>? Departed;

    public void Arrive(string id = "dev1") => Arrived?.Invoke(this, id);

    public void Depart(string id = "dev1") => Departed?.Invoke(this, id);

    public void Start() => Started = true;

    public void Stop() => Stopped = true;

    public Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        Opens++;
        Config.Disposed  = false;
        Monitor.Disposed = false;
        return Task.FromResult(new HidDevicePair(Config, Monitor));
    }
}
```

`tests/Layers.Tests/Fakes/Eventually.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace Layers.Tests.Fakes;

/// Polls a condition, optionally advancing fake time between polls, so async device code can settle.
internal static class Eventually
{
    public static async Task Until(Func<bool> condition, FakeTimeProvider? time = null, int stepMs = 1, int attempts = 1000)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return;
            }

            time?.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(2);
        }

        Assert.Fail("condition not met in time");
    }

    /// Lets queued continuations run without advancing fake time.
    public static Task Settle() => Task.Delay(50);
}
```

- [ ] **Step 2: Write the failing tests** `tests/Layers.Tests/DeviceServiceTests.cs`

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Layers.Tests;

[TestClass]
public sealed class DeviceServiceTests
{
    private FakeHidDeviceSource _source = null!;
    private FakeTimeProvider _time = null!;
    private DeviceService _service = null!;
    private List<DeviceState> _events = null!;

    [TestInitialize]
    public void Setup()
    {
        _source  = new FakeHidDeviceSource();
        _time    = new FakeTimeProvider();
        _service = new DeviceService(_source, _time);
        _events  = [];
        _service.StateChanged += (_, state) => { lock (_events) { _events.Add(state); } };
        _service.Start();
    }

    [TestCleanup]
    public async Task Cleanup() => await _service.DisposeAsync();

    private FakeConfigChannel Config => _source.Config;

    private async Task ConnectAsync(DeviceStatus expected = DeviceStatus.Connected)
    {
        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == expected);
    }

    // =========================================================================
    // CONNECT
    // =========================================================================

    [TestMethod]
    public void Start_StartsSourceAndBeginsDisconnected()
    {
        Assert.IsTrue(_source.Started);
        Assert.AreEqual(DeviceState.Initial, _service.State);
    }

    [TestMethod]
    public async Task Connect_SendsSequenceInOrder()
    {
        await ConnectAsync();

        byte[] expected = [3, 21, 21, 21, 21, 21, 21, 21, 21, 20, 11, 22];
        CollectionAssert.AreEqual(expected, Config.Commands.ToArray());
        Assert.AreEqual((byte)0, Config.Sent[9][3], "appends into the first empty slot");
        Assert.IsTrue(Config.MonitorEnabled);
        Assert.AreEqual(new DeviceState(DeviceStatus.Connected, LayerMask.Base), _service.State);
    }

    [TestMethod]
    public async Task Connect_WrongVersionWritesNothing()
    {
        Config.Version = 19;

        await ConnectAsync(DeviceStatus.VersionMismatch);
        await Eventually.Settle();

        CollectionAssert.AreEqual(new byte[] { 3 }, Config.Commands.ToArray());
    }

    [TestMethod]
    public async Task Connect_AllSlotsFullIsNoSlotWithoutAppend()
    {
        Config.FillAllSlots();

        await ConnectAsync(DeviceStatus.NoSlot);

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Config.Commands[^1]);
    }

    [TestMethod]
    public async Task Connect_ReusesExistingExpression()
    {
        Config.Slots[3] = (3, Protocol.ExpressionBytes.ToArray());

        await ConnectAsync();

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
    }

    // =========================================================================
    // LIVE REPORTS
    // =========================================================================

    [TestMethod]
    public async Task LiveReports_ChangePosted()
    {
        await ConnectAsync();

        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));

        Assert.AreEqual(new LayerMask(0b100), _service.State.Layers);
    }

    [TestMethod]
    public async Task LiveReports_SameMaskIgnored()
    {
        await ConnectAsync();
        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
        var count = _events.Count;

        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));

        Assert.AreEqual(count, _events.Count);
    }

    [TestMethod]
    public async Task LiveReports_MalformedIgnored()
    {
        await ConnectAsync();
        var count = _events.Count;

        _source.Monitor.Raise([Protocol.ReportIdMonitor, 0]);
        _source.Monitor.Raise([]);
        _source.Monitor.Raise(Replies.Monitor((0x0009_0001u, 1)));

        Assert.AreEqual(count, _events.Count);
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    // =========================================================================
    // RE-VERIFY
    // =========================================================================

    [TestMethod]
    public async Task Reverify_PresentDoesNothing()
    {
        await ConnectAsync();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);

        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression));
    }

    [TestMethod]
    public async Task Reverify_OneMissDoesNothing()
    {
        await ConnectAsync();
        Config.WipeSlots();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        await Eventually.Settle();

        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression));
    }

    [TestMethod]
    public async Task Reverify_TwoMissesReinstallAndResetLayer()
    {
        await ConnectAsync();
        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
        Config.WipeSlots();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
        await Eventually.Until(() => _service.State.Layers == LayerMask.Base);

        Assert.AreEqual(2, Config.Count(Protocol.CmdResume));
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    [TestMethod]
    public async Task Reverify_FailedReinstallIsNotFatal()
    {
        await ConnectAsync();
        Config.WipeSlots();
        Config.SendFailure = new IOException("device busy");

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Settle();
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Settle();

        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);

        Config.SendFailure = null;
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) > 8);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
    }

    [TestMethod]
    public async Task Reverify_SkippedWhenNoSlot()
    {
        Config.FillAllSlots();
        await ConnectAsync(DeviceStatus.NoSlot);

        _time.Advance(DeviceService.VerifyInterval * 3);
        await Eventually.Settle();

        Assert.AreEqual(8, Config.Count(Protocol.CmdGetExpression));
    }

    // =========================================================================
    // RETRIES AND ERRORS
    // =========================================================================

    [TestMethod]
    public async Task Read_RetriesShortReplies()
    {
        Config.ShortReplies = 9;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 50);

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Read_RejectsBadCrcAndRetries()
    {
        Config.CorruptNextReply = true;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 5);

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Read_GivesUpAfterTenAttemptsThenReconnects()
    {
        Config.ShortReplies = DeviceService.ReadAttempts;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 100);

        Assert.AreEqual(2, _source.Opens);
    }

    [TestMethod]
    public async Task SessionError_RetriesAfterDelay()
    {
        Config.SendFailure = new IOException("gone");

        _source.Arrive();
        await Eventually.Until(() => Config.SendAttempts >= 1 && Config.Disposed);
        Assert.AreEqual(DeviceStatus.Disconnected, _service.State.Status);

        Config.SendFailure = null;
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 100);
        Assert.IsTrue(_source.Opens >= 2);
    }

    // =========================================================================
    // PLUG, UNPLUG, SHUTDOWN
    // =========================================================================

    [TestMethod]
    public async Task Departed_ShowsDisconnectedAndArrivedReconnects()
    {
        await ConnectAsync();

        _source.Depart();
        Assert.AreEqual(DeviceState.Initial, _service.State);
        await Eventually.Until(() => Config.Disposed && _source.Monitor.Disposed);

        await ConnectAsync();
        Assert.AreEqual(2, _source.Opens);
    }

    [TestMethod]
    public async Task Departed_OtherDeviceIgnored()
    {
        await ConnectAsync();

        _source.Depart("someone-else");

        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    [TestMethod]
    public async Task SecondArrival_WhileConnectedIgnored()
    {
        await ConnectAsync();

        _source.Arrive("dev2");
        await Eventually.Settle();

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Dispose_TurnsMonitorOffAndClosesChannels()
    {
        await ConnectAsync();

        await _service.DisposeAsync();

        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Config.Sent[^1][2]);
        Assert.AreEqual((byte)0, Config.Sent[^1][3]);
        Assert.IsTrue(Config.Disposed);
        Assert.IsTrue(_source.Monitor.Disposed);
        Assert.IsTrue(_source.Stopped);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        await _service.DisposeAsync();
        await _service.DisposeAsync();
    }

    [TestMethod]
    public async Task NeverSendsPersistClearOrSuspend()
    {
        await ConnectAsync();
        Config.WipeSlots();
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
        await _service.DisposeAsync();

        byte[] allowed = [Protocol.CmdGetConfig, Protocol.CmdResume, Protocol.CmdAppendToExpression, Protocol.CmdGetExpression, Protocol.CmdSetMonitorEnabled];
        Assert.IsTrue(Config.Commands.All(allowed.Contains));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors for `IHidChannel`, `DeviceService`, and the other missing types.

- [ ] **Step 4: Implement `src/Layers.Core/Services/Hid.cs`**

```csharp
namespace Layers.Core.Services;

/// <summary>
/// One open HID top-level collection.
/// </summary>
/// <remarks>
/// The HID Remapper exposes config (feature reports) and monitor (input reports) as two collections, which Windows
/// shows as two devices. <c>WinRtHidChannel</c> is the real implementation. Tests use a firmware fake.
/// </remarks>
public interface IHidChannel : IDisposable
{
    /// <summary>Raised on a background thread for each input report, including the report ID byte.</summary>
    event EventHandler<byte[]>? InputReport;

    /// <summary>Sends a feature report.</summary>
    /// <remarks>The first byte is the report ID.</remarks>
    /// <param name="report">The report.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when sent.</returns>
    Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken);

    /// <summary>Reads a feature report.</summary>
    /// <remarks>The result includes the report ID byte. It may be short if the device hasn't answered yet.</remarks>
    /// <param name="reportId">The report ID.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The report bytes.</returns>
    Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken);
}

/// <summary>
/// The config and monitor channels of one physical HID Remapper.
/// </summary>
/// <remarks>
/// Disposing the pair closes both channels.
/// </remarks>
/// <param name="config">The config collection (usage 0x20).</param>
/// <param name="monitor">The monitor collection (usage 0x21).</param>
public sealed class HidDevicePair(IHidChannel config, IHidChannel monitor) : IDisposable
{
    /// <summary>Gets the config channel.</summary>
    public IHidChannel Config { get; } = config;

    /// <summary>Gets the monitor channel.</summary>
    public IHidChannel Monitor { get; } = monitor;

    /// <inheritdoc/>
    public void Dispose()
    {
        Config.Dispose();
        Monitor.Dispose();
    }
}

/// <summary>
/// Finds HID Remapper devices and opens them.
/// </summary>
/// <remarks>
/// <see cref="Arrived"/> and <see cref="Departed"/> carry the config collection's device ID.
/// </remarks>
public interface IHidDeviceSource
{
    /// <summary>Raised when a config collection appears, including ones present at <see cref="Start"/>.</summary>
    event EventHandler<string>? Arrived;

    /// <summary>Raised when a config collection disappears.</summary>
    event EventHandler<string>? Departed;

    /// <summary>Starts watching.</summary>
    /// <remarks>Arrivals for devices already plugged in are raised after this call.</remarks>
    void Start();

    /// <summary>Stops watching.</summary>
    /// <remarks>Safe to call more than once.</remarks>
    void Stop();

    /// <summary>Opens both collections of a device.</summary>
    /// <remarks>The monitor collection is matched to the config collection's physical device.</remarks>
    /// <param name="deviceId">The config collection's device ID.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open pair.</returns>
    Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The device didn't answer as the protocol requires.
/// </summary>
/// <remarks>
/// Ends the session. <c>DeviceService</c> shows Disconnected and retries after 2 seconds.
/// </remarks>
public sealed class DeviceProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DeviceProtocolException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public DeviceProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public DeviceProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
```

- [ ] **Step 5: Implement `src/Layers.Core/Services/DeviceService.cs`**

```csharp
using System.Diagnostics;
using Layers.Core.Logic;

namespace Layers.Core.Services;

/// <summary>
/// Keeps a live link to the HID Remapper and reports its status and active layers.
/// </summary>
/// <remarks>
/// <para>
/// On arrival it opens both collections, checks the config version is 18, claims a slot (reusing ours, else the first
/// empty one), appends <c>layer_state 0xFF000001 monitor</c>, sends RESUME, and enables Monitor mode. Layer changes then
/// arrive as input reports.
/// </para>
/// <para>
/// Every 2 seconds it checks our slot still holds the expression. The web config tool's "load config" sends
/// CLEAR_EXPRESSIONS, which wipes it. After 2 misses in a row it reinstalls and resets the layer to 0, because RESUME
/// resets the firmware's layer state. Waiting for 2 misses keeps the append clear of the tool's CLEAR, write, PERSIST
/// burst, so our expression never lands in the user's flash.
/// </para>
/// <para>
/// Any session error shows Disconnected and retries after 2 seconds while the device is present. Unplugging cancels
/// the session. <see cref="StateChanged"/> is raised on background threads, in order, under an internal lock:
/// handlers must be quick and must marshal to the UI thread themselves.
/// </para>
/// </remarks>
public sealed class DeviceService : IAsyncDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>Consecutive failed checks before reinstalling.</summary>
    public const int MissesBeforeReinstall = 2;

    /// <summary>Feature report read attempts before a session fails.</summary>
    public const int ReadAttempts = 10;

    private static readonly TimeSpan FirstRetryDelay   = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MonitorOffTimeout = TimeSpan.FromMilliseconds(500);

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly IHidDeviceSource _source;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DeviceState _state = DeviceState.Initial;
    private string? _deviceId;
    private CancellationTokenSource? _sessionCancel;
    private Task _sessionTask = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <remarks>
    /// Nothing happens until <see cref="Start"/>.
    /// </remarks>
    /// <param name="source">Finds and opens devices.</param>
    /// <param name="time">Clock for retries and checks. Tests pass a fake.</param>
    public DeviceService(IHidDeviceSource source, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);

        _source = source;
        _time   = time;
    }

    /// <summary>Raised when the status or layers change.</summary>
    public event EventHandler<DeviceState>? StateChanged;

    /// <summary>Gets the delay before retrying a failed session.</summary>
    public static TimeSpan ReconnectDelay { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets how often our expression is checked.</summary>
    public static TimeSpan VerifyInterval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the current state.</summary>
    public DeviceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    /// <summary>
    /// Starts watching for the device.
    /// </summary>
    /// <remarks>
    /// Call once.
    /// </remarks>
    public void Start()
    {
        _source.Arrived  += OnArrived;
        _source.Departed += OnDeparted;
        _source.Start();
    }

    /// <summary>
    /// Stops watching, turns Monitor mode off, and closes the device.
    /// </summary>
    /// <remarks>
    /// The expression stays in device RAM, where it's inert once Monitor is off. Removing it would need
    /// CLEAR_EXPRESSIONS, which would destroy the user's own expressions. Safe to call more than once.
    /// </remarks>
    /// <returns>A task that completes when the session has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        Task session;
        CancellationTokenSource? cancel;

        // Stop Accepting Work
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed      = true;
            session        = _sessionTask;
            cancel         = _sessionCancel;
            _sessionCancel = null;
            _deviceId      = null;
        }

        _source.Arrived  -= OnArrived;
        _source.Departed -= OnDeparted;
        _source.Stop();

        // End The Session
        if (cancel is not null)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
        }

        await session.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancel?.Dispose();
    }

    private void OnArrived(object? sender, string deviceId)
    {
        lock (_gate)
        {
            if (_disposed || _deviceId is not null)
            {
                return;
            }

            _deviceId      = deviceId;
            _sessionCancel = new CancellationTokenSource();
            var token      = _sessionCancel.Token;
            _sessionTask   = Task.Run(() => RunDeviceAsync(deviceId, token), CancellationToken.None);
        }
    }

    private void OnDeparted(object? sender, string deviceId)
    {
        lock (_gate)
        {
            if (_deviceId != deviceId)
            {
                return;
            }

            // Cancel First So The Ending Session Can't Publish Over This
            _deviceId = null;
            _sessionCancel?.Cancel();
            _sessionCancel = null;
            SetStateLocked(DeviceState.Initial);
        }
    }

    // =========================================================================
    // SESSIONS
    // =========================================================================

    private async Task RunDeviceAsync(string deviceId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(deviceId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Any device failure ends the session and retries; nothing may escape to the UI
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning("device session failed: {0}", ex.Message);
            }

            // Back Off Then Retry
            Publish(DeviceState.Initial, token);
            try
            {
                await Task.Delay(ReconnectDelay, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(string deviceId, CancellationToken token)
    {
        using var pair = await _source.OpenAsync(deviceId, token).ConfigureAwait(false);

        // Version Gate
        var version = await ReadAsync(pair.Config, Protocol.GetConfig(), Protocol.TryParseConfigVersion, token).ConfigureAwait(false);
        if (version != Protocol.ConfigVersion)
        {
            Publish(new DeviceState(DeviceStatus.VersionMismatch, LayerMask.Base), token);
            await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false);
            return;
        }

        // Install And Announce
        var (status, slot) = await InstallAsync(pair.Config, token).ConfigureAwait(false);
        Publish(new DeviceState(status, LayerMask.Base), token);

        // Live Layer Reports
        void OnInput(object? sender, byte[] report)
        {
            if (!Protocol.TryParseMonitorReport(report, out var layers))
            {
                return;
            }

            lock (_gate)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SetStateLocked(_state with { Layers = layers });
            }
        }

        pair.Monitor.InputReport += OnInput;
        try
        {
            // Periodic Re-Verify
            using var timer = new PeriodicTimer(VerifyInterval, _time);
            var misses      = 0;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (State.Status != DeviceStatus.Connected)
                {
                    continue;
                }

                if (slot is byte current && await SlotHoldsOursAsync(pair.Config, current, token).ConfigureAwait(false))
                {
                    misses = 0;
                    continue;
                }

                if (++misses < MissesBeforeReinstall)
                {
                    continue;
                }

                // Reinstall After Consecutive Misses
                misses = 0;
                try
                {
                    (status, slot) = await InstallAsync(pair.Config, token).ConfigureAwait(false);
                    Publish(new DeviceState(status, LayerMask.Base), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.TraceWarning("reinstall failed: {0}", ex.Message);
                }
            }
        }
        finally
        {
            pair.Monitor.InputReport -= OnInput;
            await SendMonitorOffAsync(pair.Config).ConfigureAwait(false);
        }
    }

    // =========================================================================
    // DEVICE COMMANDS
    // =========================================================================

    private async Task<(DeviceStatus Status, byte? Slot)> InstallAsync(IHidChannel config, CancellationToken token)
    {
        // Read All Slots
        var slots = new List<SlotContents>(Protocol.ExpressionSlots);
        for (byte i = 0; i < Protocol.ExpressionSlots; i++)
        {
            slots.Add(await ReadAsync(config, Protocol.GetExpression(i), Protocol.TryParseExpressionResponse, token).ConfigureAwait(false));
        }

        // Append Into An Empty Slot
        var choice = Protocol.ChooseSlot(slots);
        if (choice.Kind == SlotChoiceKind.Empty)
        {
            await config.SendFeatureAsync(Protocol.AppendExpression(choice.Slot), token).ConfigureAwait(false);
            await config.SendFeatureAsync(Protocol.Resume(), token).ConfigureAwait(false);
        }

        // Monitor On
        await config.SendFeatureAsync(Protocol.SetMonitorEnabled(true), token).ConfigureAwait(false);

        return choice.Kind == SlotChoiceKind.NoneFree
            ? (DeviceStatus.NoSlot, null)
            : (DeviceStatus.Connected, choice.Slot);
    }

    private async Task<bool> SlotHoldsOursAsync(IHidChannel config, byte slot, CancellationToken token)
    {
        try
        {
            var contents = await ReadAsync(config, Protocol.GetExpression(slot), Protocol.TryParseExpressionResponse, token).ConfigureAwait(false);
            return contents.IsOurs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Failed Read Counts As A Miss
            Trace.TraceInformation("verify read failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task<T> ReadAsync<T>(IHidChannel config, byte[] request, TryParse<T> parse, CancellationToken token)
    {
        await config.SendFeatureAsync(request, token).ConfigureAwait(false);

        // The Firmware Answers Asynchronously, So Retry With A Doubling Delay
        var delay = FirstRetryDelay;
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                var reply = await config.GetFeatureAsync(Protocol.ReportIdConfig, token).ConfigureAwait(false);
                if (reply.Length >= Protocol.PacketLength && parse(reply, out var value))
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceInformation("feature read failed: {0}", ex.Message);
            }

            await Task.Delay(delay, _time, token).ConfigureAwait(false);
            delay *= 2;
        }

        throw new DeviceProtocolException("no valid response after 10 retries");
    }

    private async Task SendMonitorOffAsync(IHidChannel config)
    {
        using var timeout = new CancellationTokenSource(MonitorOffTimeout, _time);
        try
        {
            await config.SendFeatureAsync(Protocol.SetMonitorEnabled(false), timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the device may already be gone
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceInformation("monitor off failed: {0}", ex.Message);
        }
    }

    // =========================================================================
    // PUBLISHING
    // =========================================================================

    private void Publish(DeviceState next, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            SetStateLocked(next);
        }
    }

    private void SetStateLocked(DeviceState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

Then run the device tests 20 times to catch flakiness (Git Bash):

```bash
for i in $(seq 20); do dotnet test tests/Layers.Tests --no-build --filter "FullyQualifiedName~DeviceServiceTests" || break; done
```

Every run must pass. If a test is flaky, fix the test's synchronization or a real race in the service. Never add sleeps to production code.

---

### Task 8: WinRT HID Channel, Device Source, and Hardware Probe

**Files:**
- Create: `src/Layers.Core/Services/WinRtHid.cs`
- Test: `tests/Layers.Tests/HardwareTests.cs`

**Interfaces:**
- Consumes: `IHidChannel`, `IHidDeviceSource`, `HidDevicePair`, `DeviceProtocolException`, `DeviceService`, `Protocol`.
- Produces:
  - `sealed class WinRtHidChannel(Windows.Devices.HumanInterfaceDevice.HidDevice device) : IHidChannel`
  - `sealed class WinRtHidDeviceSource : IHidDeviceSource` with `static Task<IReadOnlyList<string>> FindConfigDeviceIdsAsync()`

This task answers the spec's open question: can WinRT HID open these vendor collections from an unpackaged process? The test process is unpackaged, so the hardware test is the probe.

- [ ] **Step 1: Write the hardware tests** `tests/Layers.Tests/HardwareTests.cs`

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Tests;

/// Needs a real HID Remapper on config version 18. Excluded from the default run.
[TestClass]
public sealed class HardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    private static async Task RequireDeviceAsync()
    {
        if ((await WinRtHidDeviceSource.FindConfigDeviceIdsAsync()).Count == 0)
        {
            Assert.Inconclusive("No HID Remapper connected");
        }
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(30_000)]
    public async Task RealDevice_Connects()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        var settled             = new TaskCompletionSource<DeviceState>();
        service.StateChanged   += (_, state) =>
        {
            if (state.Status != DeviceStatus.Disconnected)
            {
                settled.TrySetResult(state);
            }
        };

        service.Start();
        var result = await settled.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(DeviceStatus.Connected, result.Status, $"device reported {result.Status}");
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [TestCategory("Manual")]
    [Timeout(60_000)]
    public async Task RealDevice_ReportsLayerChange()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        var changed             = new TaskCompletionSource<LayerMask>();
        service.StateChanged   += (_, state) =>
        {
            if (state.Status == DeviceStatus.Connected && state.Layers != LayerMask.Base)
            {
                changed.TrySetResult(state.Layers);
            }
        };

        service.Start();
        TestContext.WriteLine("Switch to a non-zero layer on the device within 45 seconds.");
        var layers = await changed.Task.WaitAsync(TimeSpan.FromSeconds(45));

        TestContext.WriteLine($"Saw {layers.Label}");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Layers.slnx`
Expected: build error, `WinRtHidDeviceSource` not found.

- [ ] **Step 3: Implement `src/Layers.Core/Services/WinRtHid.cs`**

```csharp
using System.Runtime.InteropServices.WindowsRuntime;
using Layers.Core.Logic;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;

namespace Layers.Core.Services;

/// <summary>
/// An <see cref="IHidChannel"/> over the stock WinRT <see cref="HidDevice"/>.
/// </summary>
/// <remarks>
/// Report buffers include the report ID as their first byte, matching the protocol layout.
/// </remarks>
/// <param name="device">The open device. Owned and disposed by this channel.</param>
public sealed class WinRtHidChannel(HidDevice device) : IHidChannel
{
    private EventHandler<byte[]>? _inputReport;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? InputReport
    {
        add
        {
            if (_inputReport is null)
            {
                device.InputReportReceived += OnInputReportReceived;
            }

            _inputReport += value;
        }
        remove
        {
            _inputReport -= value;
            if (_inputReport is null)
            {
                device.InputReportReceived -= OnInputReportReceived;
            }
        }
    }

    /// <inheritdoc/>
    public async Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var feature  = device.CreateFeatureReport(report[0]);
        feature.Data = report.AsBuffer();
        await device.SendFeatureReportAsync(feature).AsTask(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        var feature = await device.GetFeatureReportAsync(reportId).AsTask(cancellationToken).ConfigureAwait(false);
        return feature.Data.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        device.InputReportReceived -= OnInputReportReceived;
        _inputReport = null;
        device.Dispose();
    }

    private void OnInputReportReceived(HidDevice sender, HidInputReportReceivedEventArgs args) =>
        _inputReport?.Invoke(this, args.Report.Data.ToArray());
}

/// <summary>
/// Finds HID Remapper devices with a stock <see cref="DeviceWatcher"/>.
/// </summary>
/// <remarks>
/// Watches the config collection (usage page 0xFF00, usage 0x20). On open, the monitor collection (usage 0x21) is
/// matched by <c>System.Devices.ContainerId</c>, which Windows assigns per physical device, so two attached
/// HID Remappers never cross wires.
/// </remarks>
public sealed class WinRtHidDeviceSource : IHidDeviceSource
{
    private const string ContainerIdProperty = "System.Devices.ContainerId";

    private DeviceWatcher? _watcher;

    /// <inheritdoc/>
    public event EventHandler<string>? Arrived;

    /// <inheritdoc/>
    public event EventHandler<string>? Departed;

    /// <summary>
    /// Lists the config collections present now.
    /// </summary>
    /// <remarks>
    /// Used by the hardware tests to skip when no device is attached.
    /// </remarks>
    /// <returns>Device IDs.</returns>
    public static async Task<IReadOnlyList<string>> FindConfigDeviceIdsAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.ConfigUsage));
        return devices.Select(device => device.Id).ToList();
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        // DeviceWatcher Needs Added, Removed, And Updated Handlers To Run
        _watcher          = DeviceInformation.CreateWatcher(HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.ConfigUsage));
        _watcher.Added   += (_, info) => Arrived?.Invoke(this, info.Id);
        _watcher.Removed += (_, update) => Departed?.Invoke(this, update.Id);
        _watcher.Updated += (_, _) => { };
        _watcher.Start();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            _watcher.Stop();
        }

        _watcher = null;
    }

    /// <inheritdoc/>
    public async Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        // Open Config
        var config = await HidDevice.FromIdAsync(deviceId, FileAccessMode.ReadWrite).AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceProtocolException("config collection could not be opened");

        try
        {
            // Find The Monitor Collection On The Same Physical Device
            var configInfo = await DeviceInformation.CreateFromIdAsync(deviceId, [ContainerIdProperty]).AsTask(cancellationToken).ConfigureAwait(false);
            var container  = configInfo.Properties.TryGetValue(ContainerIdProperty, out var value) ? value : null;
            var monitors   = await DeviceInformation.FindAllAsync(
                HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.MonitorUsage),
                [ContainerIdProperty]).AsTask(cancellationToken).ConfigureAwait(false);
            var monitorInfo = monitors.FirstOrDefault(info => info.Properties.TryGetValue(ContainerIdProperty, out var id) && Equals(id, container))
                ?? throw new DeviceProtocolException("monitor collection not found");

            // Open Monitor
            var monitor = await HidDevice.FromIdAsync(monitorInfo.Id, FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false)
                ?? throw new DeviceProtocolException("monitor collection could not be opened");

            return new HidDevicePair(new WinRtHidChannel(config), new WinRtHidChannel(monitor));
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }
}
```

- [ ] **Step 4: Run the default suite**

Run: `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: all pass.

- [ ] **Step 5: Run the automatic hardware probe**

Run: `dotnet test tests/Layers.Tests --filter "FullyQualifiedName~RealDevice_Connects"`

Report the result verbatim:
- **Passed:** WinRT HID works unpackaged. Continue.
- **Skipped as Inconclusive "No HID Remapper connected":** report it. The owner runs the probe later. Continue.
- **Failed** (open returns null, `UnauthorizedAccessException`, or a timeout): stop and report `BLOCKED` with the exact error. The fallback is a `Win32HidChannel` using `CreateFile` plus `HidD_SetFeature`/`HidD_GetFeature`/`ReadFile` via CsWin32, behind the same `IHidChannel`. The controller decides before any fallback is written.

`RealDevice_ReportsLayerChange` is manual. The owner runs it in Task 17.

---

### Task 9: Tray Icon Renderer and Tray Message Router

**Files:**
- Modify: `src/Layers.Core/NativeMethods.txt`
- Create: `src/Layers.Core/Services/TrayIconRenderer.cs`, `src/Layers.Core/Services/TrayMessageRouter.cs`
- Test: `tests/Layers.Tests/TrayIconRendererTests.cs`, `tests/Layers.Tests/TrayMessageRouterTests.cs`

**Interfaces:**
- Consumes: `IconMath`, `AlphaBuffer`, `DeviceState`, `DeviceStatus`, `LayerMask`.
- Produces:
  - `static class TrayIconRenderer` with `const string LayersGlyph = ""`, `byte[] RenderBgra(DeviceState state, bool lightTaskbar, int size)`, `DestroyIconSafeHandle CreateIcon(byte[] bgra, int size)`, and `internal static AlphaBuffer Rasterize(string text, string family, bool bold, int size)`.
  - `enum TrayAction { None, OpenMenu, ReAddIcon, Refresh, Quit }`.
  - `static class TrayMessageRouter` with `const uint TrayCallbackMessage = 0x8001` (`WM_APP + 1`) and `TrayAction Route(uint message, nint lParam, uint taskbarCreatedMessage)`.

- [ ] **Step 1: Append to `src/Layers.Core/NativeMethods.txt`**

```
CreateCompatibleDC
DeleteDC
CreateDIBSection
CreateBitmap
CreateFont
SelectObject
DeleteObject
SetBkMode
SetTextColor
DrawText
GdiFlush
CreateIconIndirect
DestroyIcon
GetGuiResources
GetCurrentProcess
BITMAPINFO
ICONINFO
WM_APP
WM_LBUTTONUP
WM_RBUTTONUP
WM_SETTINGCHANGE
WM_DPICHANGED
WM_CLOSE
```

- [ ] **Step 2: Write the failing tests**

`tests/Layers.Tests/TrayIconRendererTests.cs`:

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.Tests;

[TestClass]
public sealed class TrayIconRendererTests
{
    private static readonly DeviceState Base = new(DeviceStatus.Connected, LayerMask.Base);

    private static DeviceState Layer(int n) => new(DeviceStatus.Connected, new LayerMask((byte)(1 << n)));

    private static AlphaBuffer AlphaOf(byte[] bgra, int size)
    {
        var alpha = new AlphaBuffer(size, size);
        for (var i = 0; i < alpha.Pixels.Length; i++)
        {
            alpha.Pixels[i] = bgra[i * 4 + 3];
        }
        return alpha;
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void RenderBgra_HasFourBytesPerPixel(int size)
    {
        Assert.AreEqual(size * size * 4, TrayIconRenderer.RenderBgra(Base, false, size).Length);
    }

    [TestMethod]
    public void LayerZero_DrawsGlyphNotDigit()
    {
        var glyph = TrayIconRenderer.RenderBgra(Base, false, 16);
        var digit = TrayIconRenderer.RenderBgra(Layer(3), false, 16);

        Assert.IsNotNull(AlphaOf(glyph, 16).InkBounds());
        CollectionAssert.AreNotEqual(glyph, digit);
    }

    [TestMethod]
    [DataRow(DeviceStatus.Disconnected)]
    [DataRow(DeviceStatus.NoSlot)]
    [DataRow(DeviceStatus.VersionMismatch)]
    public void NotConnected_DrawsGlyphEvenWithLayers(DeviceStatus status)
    {
        var glyph    = TrayIconRenderer.RenderBgra(Base, false, 16);
        var degraded = TrayIconRenderer.RenderBgra(new DeviceState(status, new LayerMask(0b1000)), false, 16);

        CollectionAssert.AreEqual(glyph, degraded);
    }

    [TestMethod]
    public void EachDigit_HasInkAndDiffers()
    {
        var seen = new HashSet<string>();
        for (var n = 1; n < 8; n++)
        {
            var bgra = TrayIconRenderer.RenderBgra(Layer(n), false, 16);
            Assert.IsNotNull(AlphaOf(bgra, 16).InkBounds(), $"digit {n} has no ink");
            Assert.IsTrue(seen.Add(Convert.ToBase64String(bgra)), $"digit {n} renders like another digit");
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void Digit_IsCenteredWithinOnePixel(int size)
    {
        var bounds = AlphaOf(TrayIconRenderer.RenderBgra(Layer(3), false, size), size).InkBounds()!.Value;

        var left   = bounds.MinX;
        var right  = size - 1 - bounds.MaxX;
        var top    = bounds.MinY;
        var bottom = size - 1 - bounds.MaxY;

        Assert.IsTrue(Math.Abs(left - right) <= 1, $"left {left} right {right}");
        Assert.IsTrue(Math.Abs(top - bottom) <= 1, $"top {top} bottom {bottom}");
    }

    [TestMethod]
    [DataRow(false, (byte)0xFF)]
    [DataRow(true, (byte)0x19)]
    public void RenderBgra_TintFollowsTaskbarTheme(bool light, byte shade)
    {
        var bgra   = TrayIconRenderer.RenderBgra(Layer(3), light, 16);
        var opaque = Enumerable.Range(0, 16 * 16).Where(i => bgra[i * 4 + 3] == 255).ToList();

        Assert.IsTrue(opaque.Count > 0);
        foreach (var i in opaque)
        {
            Assert.AreEqual(shade, bgra[i * 4]);
            Assert.AreEqual(shade, bgra[i * 4 + 1]);
            Assert.AreEqual(shade, bgra[i * 4 + 2]);
        }
    }

    [TestMethod]
    public void CreateIcon_ReturnsValidHandle()
    {
        using var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Base, false, 16), 16);

        Assert.IsFalse(icon.IsInvalid);
    }

    [TestMethod]
    [DoNotParallelize]
    public void RenderAndCreate_ThousandTimesDoesNotLeakGdi()
    {
        static uint GdiCount() => PInvoke.GetGuiResources(PInvoke.GetCurrentProcess(), GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS);

        // Warm Up Font Caches
        using (TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Layer(1), false, 16), 16))
        {
        }

        var before = GdiCount();
        for (var i = 0; i < 1000; i++)
        {
            using var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Layer(i % 8), i % 2 == 0, 16), 16);
        }

        var after = GdiCount();
        Assert.IsTrue(after <= before + 2, $"GDI objects grew from {before} to {after}");
    }
}
```

`tests/Layers.Tests/TrayMessageRouterTests.cs`:

```csharp
using Layers.Core.Services;

namespace Layers.Tests;

[TestClass]
public sealed class TrayMessageRouterTests
{
    private const uint TaskbarCreated = 0xC123;

    [TestMethod]
    public void CallbackMessage_IsWmAppPlusOne()
    {
        Assert.AreEqual(0x8001u, TrayMessageRouter.TrayCallbackMessage);
    }

    [TestMethod]
    [DataRow(0x0202)] // WM_LBUTTONUP
    [DataRow(0x0205)] // WM_RBUTTONUP
    public void Route_ButtonUpOpensMenu(int mouseMessage)
    {
        Assert.AreEqual(TrayAction.OpenMenu, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, mouseMessage, TaskbarCreated));
    }

    [TestMethod]
    [DataRow(0x0201)] // WM_LBUTTONDOWN
    [DataRow(0x0204)] // WM_RBUTTONDOWN
    [DataRow(0x0200)] // WM_MOUSEMOVE
    [DataRow(0x0203)] // WM_LBUTTONDBLCLK
    public void Route_OtherTrayMessagesIgnored(int mouseMessage)
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, mouseMessage, TaskbarCreated));
    }

    [TestMethod]
    public void Route_TaskbarCreatedReAdds()
    {
        Assert.AreEqual(TrayAction.ReAddIcon, TrayMessageRouter.Route(TaskbarCreated, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_UnregisteredTaskbarCreatedNeverMatchesZero()
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(0, 0, 0));
    }

    [TestMethod]
    [DataRow(0x001Au)] // WM_SETTINGCHANGE
    [DataRow(0x02E0u)] // WM_DPICHANGED
    public void Route_SettingAndDpiChangesRefresh(uint message)
    {
        Assert.AreEqual(TrayAction.Refresh, TrayMessageRouter.Route(message, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_CloseQuits()
    {
        // The MSI's CloseApplication and a plain taskkill send WM_CLOSE to top-level windows
        Assert.AreEqual(TrayAction.Quit, TrayMessageRouter.Route(0x0010, 0, TaskbarCreated));
    }

    [TestMethod]
    public void Route_UnrelatedIgnored()
    {
        Assert.AreEqual(TrayAction.None, TrayMessageRouter.Route(0x000F, 0, TaskbarCreated));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors.

- [ ] **Step 4: Implement `src/Layers.Core/Services/TrayMessageRouter.cs`**

```csharp
using Windows.Win32;

namespace Layers.Core.Services;

/// <summary>
/// What the tray window should do with a message.
/// </summary>
/// <remarks>
/// Returned by <see cref="TrayMessageRouter.Route"/>.
/// </remarks>
public enum TrayAction
{
    /// <summary>Pass to <c>DefWindowProc</c>.</summary>
    None,

    /// <summary>Open the tray menu.</summary>
    OpenMenu,

    /// <summary>Explorer restarted: add the icon again with <c>NIM_ADD</c>.</summary>
    ReAddIcon,

    /// <summary>Theme or DPI may have changed: redraw the icon.</summary>
    Refresh,

    /// <summary><c>WM_CLOSE</c> from the installer or <c>taskkill</c>: shut down cleanly.</summary>
    Quit,
}

/// <summary>
/// Decides what the hidden tray window does with each message.
/// </summary>
/// <remarks>
/// Kept pure so it's testable without a window. A left or right button-up on the icon opens the menu (there's no
/// separate context menu). <c>TaskbarCreated</c> means Explorer restarted. <c>WM_SETTINGCHANGE</c> (including
/// "ImmersiveColorSet" for the taskbar theme) and <c>WM_DPICHANGED</c> redraw the icon. <c>WM_CLOSE</c> quits, so the
/// MSI's <c>CloseApplication</c> can close the app cleanly before replacing files.
/// </remarks>
public static class TrayMessageRouter
{
    /// <summary>The <c>uCallbackMessage</c> registered with <c>Shell_NotifyIconW</c>: <c>WM_APP + 1</c>.</summary>
    public const uint TrayCallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>
    /// Routes one window message.
    /// </summary>
    /// <remarks>
    /// With the default <c>NOTIFYICON_VERSION</c>, the mouse message arrives in the low word of <c>lParam</c>.
    /// </remarks>
    /// <param name="message">The window message.</param>
    /// <param name="lParam">The message's lParam.</param>
    /// <param name="taskbarCreatedMessage">The registered <c>TaskbarCreated</c> message, or 0 if registration failed.</param>
    /// <returns>The action.</returns>
    public static TrayAction Route(uint message, nint lParam, uint taskbarCreatedMessage)
    {
        // Explorer Restarted
        if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
        {
            return TrayAction.ReAddIcon;
        }

        // Icon Clicks
        if (message == TrayCallbackMessage)
        {
            var mouse = (uint)(lParam & 0xFFFF);
            return mouse is PInvoke.WM_LBUTTONUP or PInvoke.WM_RBUTTONUP ? TrayAction.OpenMenu : TrayAction.None;
        }

        // Clean Shutdown Request
        if (message == PInvoke.WM_CLOSE)
        {
            return TrayAction.Quit;
        }

        // Theme And DPI
        return message is PInvoke.WM_SETTINGCHANGE or PInvoke.WM_DPICHANGED ? TrayAction.Refresh : TrayAction.None;
    }
}
```

- [ ] **Step 5: Implement `src/Layers.Core/Services/TrayIconRenderer.cs`**

CsWin32 generates the GDI signatures. The code below uses the raw-handle overloads. If a generated name or parameter type differs (for example `CreateFont` taking a `PCWSTR`, or a flag enum named differently), adapt the call to the generated API, but keep the steps and the cleanup in `finally` exactly as written. The leak test pins the cleanup.

```csharp
using System.ComponentModel;
using System.Globalization;
using Layers.Core.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.Core.Services;

/// <summary>
/// Draws the tray icon.
/// </summary>
/// <remarks>
/// <para>
/// Layer 0, or any status other than Connected, shows the Segoe Fluent Icons layers glyph (U+E81E). Otherwise the
/// highest active layer is drawn as a digit in Segoe UI Variable Display Bold, filling the whole icon: a corner badge
/// wasn't readable at 16 px.
/// </para>
/// <para>
/// Uses plain GDI, which is AOT-safe with no COM. White text is drawn with <c>ANTIALIASED_QUALITY</c> on a black
/// 32bpp DIB at 4× size, and the red channel is read back as coverage. It's then downsampled, re-centered on the
/// actual ink, and tinted: white on a dark taskbar, <c>#191919</c> on a light one. Every GDI object is released in
/// <c>finally</c>.
/// </para>
/// </remarks>
public static class TrayIconRenderer
{
    /// <summary>Segoe Fluent Icons "MapLayers".</summary>
    public const string LayersGlyph = "";

    private const string GlyphFont   = "Segoe Fluent Icons";
    private const string DigitFont   = "Segoe UI Variable Display";
    private const int Supersample    = 4;
    private const byte DarkTaskbarInk  = 0xFF;
    private const byte LightTaskbarInk = 0x19;

    /// <summary>
    /// Renders the icon's pixels.
    /// </summary>
    /// <remarks>
    /// Pure apart from GDI rasterization, so tests can inspect the pixels.
    /// </remarks>
    /// <param name="state">The device state.</param>
    /// <param name="lightTaskbar">Whether the taskbar is light.</param>
    /// <param name="size">Icon size in pixels, from <see cref="IconMath.IconSize"/>.</param>
    /// <returns>Premultiplied BGRA, top-down.</returns>
    public static byte[] RenderBgra(DeviceState state, bool lightTaskbar, int size)
    {
        // Pick Glyph Or Digit
        var badge = state.Status == DeviceStatus.Connected ? state.Layers.Badge : null;
        var large = badge is int digit
            ? Rasterize(digit.ToString(CultureInfo.InvariantCulture), DigitFont, bold: true, size * Supersample)
            : Rasterize(LayersGlyph, GlyphFont, bold: false, size * Supersample);

        // Reduce, Center, Tint
        var small = IconMath.CenterInk(IconMath.Downsample(large, Supersample));
        var ink   = lightTaskbar ? LightTaskbarInk : DarkTaskbarInk;

        return IconMath.ToPremultipliedBgra(small, ink, ink, ink);
    }

    /// <summary>
    /// Wraps premultiplied BGRA pixels in an HICON.
    /// </summary>
    /// <remarks>
    /// The color and mask bitmaps are deleted before returning, since <c>CreateIconIndirect</c> copies them.
    /// The caller owns the returned handle.
    /// </remarks>
    /// <param name="bgra">Pixels from <see cref="RenderBgra"/>.</param>
    /// <param name="size">Icon size.</param>
    /// <returns>The icon.</returns>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    public static unsafe DestroyIconSafeHandle CreateIcon(byte[] bgra, int size)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        var info  = TopDownInfo(size);
        void* bits;
        var color = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var mask  = PInvoke.CreateBitmap(size, size, 1, 1, null);
        try
        {
            if (color.IsNull || mask.IsNull)
            {
                throw new Win32Exception();
            }

            // Copy Pixels
            bgra.AsSpan().CopyTo(new Span<byte>(bits, bgra.Length));

            // Build The Icon
            var iconInfo = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            var icon     = PInvoke.CreateIconIndirect(in iconInfo);
            if (icon.IsNull)
            {
                throw new Win32Exception();
            }

            return new DestroyIconSafeHandle(icon, ownsHandle: true);
        }
        finally
        {
            PInvoke.DeleteObject(color);
            PInvoke.DeleteObject(mask);
        }
    }

    /// <summary>
    /// Draws text as coverage.
    /// </summary>
    /// <remarks>
    /// The em height equals <paramref name="size"/>, and the text is centered with <c>DT_CENTER | DT_VCENTER</c>.
    /// <see cref="IconMath.CenterInk"/> fixes the vertical offset afterwards.
    /// </remarks>
    /// <param name="text">Text or glyph.</param>
    /// <param name="family">Font family.</param>
    /// <param name="bold">Bold weight.</param>
    /// <param name="size">Square size in pixels.</param>
    /// <returns>Coverage.</returns>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    internal static unsafe AlphaBuffer Rasterize(string text, string family, bool bold, int size)
    {
        var info  = TopDownInfo(size);
        var dc    = PInvoke.CreateCompatibleDC(HDC.Null);
        void* bits;
        var bitmap = PInvoke.CreateDIBSection(dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var font   = PInvoke.CreateFont(-size, 0, 0, 0, bold ? 700 : 400, 0, 0, 0,
            FONT_CHARSET.DEFAULT_CHARSET, FONT_OUTPUT_PRECISION.OUT_TT_PRECIS, FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            FONT_QUALITY.ANTIALIASED_QUALITY, 0, family);
        var oldBitmap = HGDIOBJ.Null;
        var oldFont   = HGDIOBJ.Null;
        try
        {
            if (dc.IsNull || bitmap.IsNull || font.IsNull)
            {
                throw new Win32Exception();
            }

            // Draw White On Black
            oldBitmap = PInvoke.SelectObject(dc, bitmap);
            oldFont   = PInvoke.SelectObject(dc, font);
            PInvoke.SetBkMode(dc, BACKGROUND_MODE.TRANSPARENT);
            PInvoke.SetTextColor(dc, new COLORREF(0x00FFFFFF));
            var rect = new RECT(0, 0, size, size);
            PInvoke.DrawText(dc, text, -1, &rect, DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
            PInvoke.GdiFlush();

            // Red Channel Is Coverage
            var coverage = new AlphaBuffer(size, size);
            var pixels   = new ReadOnlySpan<byte>(bits, size * size * 4);
            for (var i = 0; i < coverage.Pixels.Length; i++)
            {
                coverage.Pixels[i] = pixels[i * 4 + 2];
            }

            return coverage;
        }
        finally
        {
            if (!oldFont.IsNull)
            {
                PInvoke.SelectObject(dc, oldFont);
            }

            if (!oldBitmap.IsNull)
            {
                PInvoke.SelectObject(dc, oldBitmap);
            }

            PInvoke.DeleteObject(font);
            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(dc);
        }
    }

    private static unsafe BITMAPINFO TopDownInfo(int size)
    {
        var info = new BITMAPINFO();
        info.bmiHeader.biSize     = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth    = size;
        info.bmiHeader.biHeight   = -size;
        info.bmiHeader.biPlanes   = 1;
        info.bmiHeader.biBitCount = 32;
        return info;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass. If `Digit_IsCenteredWithinOnePixel` fails, check `CenterInk` is applied after `Downsample`. Don't loosen the tolerance.

- [ ] **Step 7: Look at the icons**

Write a throwaway test (don't keep it) that saves `RenderBgra` output for layer 0 and layers 1–7 at 16 and 32 px, dark and light, as PNGs into the scratchpad. Read the PNGs with the Read tool, confirm U+E81E looks like stacked layers and the digits are legible, and report what you saw. If U+E81E isn't a layers-stack glyph, pick the Segoe Fluent Icons code point that is, update `LayersGlyph`, and report the change.

---

### Task 10: View Models

**Files:**
- Create: `src/Layers.Core/ViewModels/ObservableObject.cs`, `src/Layers.Core/ViewModels/TrayMenuViewModel.cs`, `src/Layers.Core/ViewModels/SettingsViewModel.cs`
- Test: `tests/Layers.Tests/TrayMenuViewModelTests.cs`, `tests/Layers.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `DeviceState`, `StatusText`, `HudSettings`, `SettingsStore`, `IStartupRegistration`, `StartupState`.
- Produces:
  - `abstract class ObservableObject : INotifyPropertyChanged` with `protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)` and `protected void OnPropertyChanged([CallerMemberName] string? name = null)`.
  - `enum StatusTone { Success, Caution, Critical }`.
  - `sealed class TrayMenuViewModel : ObservableObject` with `DeviceState State { get; set; }`, `string StatusLabel`, `string? StatusDetail`, `bool HasStatusDetail`, `string LayerLabel`, and `StatusTone Tone`.
  - `sealed class SettingsViewModel : ObservableObject` with:
    - ctor `(SettingsStore store, IStartupRegistration startup)`
    - `event EventHandler<HudSettings>? HudSettingsChanged`
    - `bool HudEnabled { get; set; }`
    - `IReadOnlyList<LayerOptionViewModel> Layers`
    - `bool StartAtSignIn`, `bool StartupControllable`, `string? StartupNote`, `bool HasStartupNote`
    - `Task LoadStartupAsync()` and `Task SetStartAtSignInAsync(bool)`
    - `string Version`
  - `sealed class LayerOptionViewModel : ObservableObject` with `int Index`, `string Label`, `bool IsShown { get; set; }`, and `bool IsEnabled`.

- [ ] **Step 1: Write the failing tests**

`tests/Layers.Tests/TrayMenuViewModelTests.cs`:

```csharp
using Layers.Core.Logic;
using Layers.Core.ViewModels;

namespace Layers.Tests;

[TestClass]
public sealed class TrayMenuViewModelTests
{
    [TestMethod]
    public void Initial_IsDisconnected()
    {
        var model = new TrayMenuViewModel();

        Assert.AreEqual("Disconnected", model.StatusLabel);
        Assert.AreEqual(StatusTone.Critical, model.Tone);
        Assert.IsFalse(model.HasStatusDetail);
        Assert.AreEqual("Layer 0", model.LayerLabel);
    }

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected", StatusTone.Success, false)]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable", StatusTone.Caution, true)]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware", StatusTone.Caution, true)]
    [DataRow(DeviceStatus.Disconnected, "Disconnected", StatusTone.Critical, false)]
    public void Status_MapsLabelToneAndDetail(DeviceStatus status, string label, StatusTone tone, bool hasDetail)
    {
        var model = new TrayMenuViewModel { State = new DeviceState(status, LayerMask.Base) };

        Assert.AreEqual(label, model.StatusLabel);
        Assert.AreEqual(tone, model.Tone);
        Assert.AreEqual(hasDetail, model.HasStatusDetail);
        Assert.AreEqual(StatusText.Detail(status), model.StatusDetail);
    }

    [TestMethod]
    public void LayerLabel_FollowsMask()
    {
        var model = new TrayMenuViewModel { State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010)) };

        Assert.AreEqual("Layers 1, 3", model.LayerLabel);
    }

    [TestMethod]
    public void StateChange_RaisesEveryDerivedProperty()
    {
        var model   = new TrayMenuViewModel();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.State = new DeviceState(DeviceStatus.NoSlot, new LayerMask(0b10));

        string[] expected = [nameof(model.State), nameof(model.StatusLabel), nameof(model.StatusDetail), nameof(model.HasStatusDetail), nameof(model.LayerLabel), nameof(model.Tone)];
        CollectionAssert.AreEquivalent(expected, changed);
    }

    [TestMethod]
    public void SameState_RaisesNothing()
    {
        var model   = new TrayMenuViewModel();
        var changed = 0;
        model.PropertyChanged += (_, _) => changed++;

        model.State = DeviceState.Initial;

        Assert.AreEqual(0, changed);
    }
}
```

`tests/Layers.Tests/SettingsViewModelTests.cs`:

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;

namespace Layers.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    private sealed class FakeStartup(StartupState state) : IStartupRegistration
    {
        public StartupState State { get; private set; } = state;
        public List<bool> Requests { get; } = [];

        public Task<StartupState> GetStateAsync() => Task.FromResult(State);

        public Task<StartupState> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            State = State with { IsEnabled = enabled };
            return Task.FromResult(State);
        }
    }

    private TempRegistryKey _key = null!;
    private SettingsStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _key   = new TempRegistryKey();
        _store = new SettingsStore(_key.Path);
    }

    [TestCleanup]
    public void Cleanup() => _key.Dispose();

    private SettingsViewModel Create(StartupState? startup = null) =>
        new(_store, new FakeStartup(startup ?? new StartupState(true, true, null)));

    [TestMethod]
    public void Loads_StoredSettings()
    {
        _store.Save(new HudSettings(false, 0b100));

        var model = Create();

        Assert.IsFalse(model.HudEnabled);
        Assert.IsFalse(model.Layers[2].IsShown);
        Assert.IsTrue(model.Layers[1].IsShown);
    }

    [TestMethod]
    public void Layers_AreZeroThroughSeven()
    {
        var model = Create();

        Assert.AreEqual(8, model.Layers.Count);
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => $"Layer {i}").ToArray(), model.Layers.Select(l => l.Label).ToArray());
    }

    [TestMethod]
    public void HudToggle_SavesAndNotifies()
    {
        var model = Create();
        HudSettings? raised = null;
        model.HudSettingsChanged += (_, s) => raised = s;

        model.HudEnabled = false;

        Assert.AreEqual(new HudSettings(false, 0), _store.Load());
        Assert.AreEqual(new HudSettings(false, 0), raised);
    }

    [TestMethod]
    public void UncheckingLayer_SetsSuppressedBit()
    {
        var model = Create();

        model.Layers[3].IsShown = false;

        Assert.AreEqual((byte)0b1000, _store.Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void CheckingLayer_ClearsSuppressedBit()
    {
        _store.Save(new HudSettings(true, 0b1001));
        var model = Create();

        model.Layers[3].IsShown = true;

        Assert.AreEqual((byte)0b0001, _store.Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void LayerOptions_DisabledWhenHudOff()
    {
        var model   = Create();
        var changed = new List<string?>();
        model.Layers[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.HudEnabled = false;

        Assert.IsTrue(model.Layers.All(l => !l.IsEnabled));
        CollectionAssert.Contains(changed, nameof(LayerOptionViewModel.IsEnabled));
    }

    [TestMethod]
    public async Task Startup_LoadsState()
    {
        var model = Create(new StartupState(false, false, "Turned off in Settings › Apps › Startup."));

        await model.LoadStartupAsync();

        Assert.IsFalse(model.StartAtSignIn);
        Assert.IsFalse(model.StartupControllable);
        Assert.IsTrue(model.HasStartupNote);
        Assert.AreEqual("Turned off in Settings › Apps › Startup.", model.StartupNote);
    }

    [TestMethod]
    public async Task Startup_ToggleCallsRegistration()
    {
        var startup = new FakeStartup(new StartupState(true, true, null));
        var model   = new SettingsViewModel(_store, startup);
        await model.LoadStartupAsync();

        await model.SetStartAtSignInAsync(false);

        CollectionAssert.AreEqual(new[] { false }, startup.Requests);
        Assert.IsFalse(model.StartAtSignIn);
    }

    [TestMethod]
    public async Task Startup_SameValueDoesNothing()
    {
        var startup = new FakeStartup(new StartupState(true, true, null));
        var model   = new SettingsViewModel(_store, startup);
        await model.LoadStartupAsync();

        await model.SetStartAtSignInAsync(true);

        Assert.AreEqual(0, startup.Requests.Count);
    }

    [TestMethod]
    public void Version_IsAssemblyVersion()
    {
        Assert.AreEqual("2.0.0", Create().Version);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Layers.slnx`
Expected: build errors.

- [ ] **Step 3: Implement `src/Layers.Core/ViewModels/ObservableObject.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Layers.Core.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for the view models.
/// </summary>
/// <remarks>
/// Stock BCL only. No MVVM library.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    /// <remarks>Defaults to the calling member's name.</remarks>
    /// <param name="name">The property name.</param>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets a field and raises <see cref="PropertyChanged"/> if it changed.</summary>
    /// <remarks>Uses <see cref="EqualityComparer{T}.Default"/>.</remarks>
    /// <typeparam name="T">Field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">The property name.</param>
    /// <returns><see langword="true"/> if the value changed.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
```

- [ ] **Step 4: Implement `src/Layers.Core/ViewModels/TrayMenuViewModel.cs`**

```csharp
using Layers.Core.Logic;

namespace Layers.Core.ViewModels;

/// <summary>
/// Color of the status dot.
/// </summary>
/// <remarks>
/// The UI maps these to <c>SystemFillColorSuccessBrush</c>, <c>SystemFillColorCautionBrush</c>, and
/// <c>SystemFillColorCriticalBrush</c>.
/// </remarks>
public enum StatusTone
{
    /// <summary>Connected.</summary>
    Success,

    /// <summary>NoSlot or VersionMismatch.</summary>
    Caution,

    /// <summary>Disconnected.</summary>
    Critical,
}

/// <summary>
/// Backs the tray menu's status and layer rows.
/// </summary>
/// <remarks>
/// Setting <see cref="State"/> raises change notifications for every derived property, so an open menu updates live.
/// </remarks>
public sealed class TrayMenuViewModel : ObservableObject
{
    private DeviceState _state = DeviceState.Initial;

    /// <summary>Gets or sets the device state.</summary>
    public DeviceState State
    {
        get => _state;
        set
        {
            if (!SetField(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(HasStatusDetail));
            OnPropertyChanged(nameof(LayerLabel));
            OnPropertyChanged(nameof(Tone));
        }
    }

    /// <summary>Gets the status row text.</summary>
    public string StatusLabel => StatusText.Label(_state.Status);

    /// <summary>Gets the degraded-state explanation, if any.</summary>
    public string? StatusDetail => StatusText.Detail(_state.Status);

    /// <summary>Gets a value indicating whether the detail row shows.</summary>
    public bool HasStatusDetail => StatusDetail is not null;

    /// <summary>Gets the layer row text.</summary>
    public string LayerLabel => _state.Layers.Label;

    /// <summary>Gets the status dot color.</summary>
    public StatusTone Tone => _state.Status switch
    {
        DeviceStatus.Connected    => StatusTone.Success,
        DeviceStatus.Disconnected => StatusTone.Critical,
        _                         => StatusTone.Caution,
    };
}
```

- [ ] **Step 5: Implement `src/Layers.Core/ViewModels/SettingsViewModel.cs`**

```csharp
using System.Reflection;
using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Core.ViewModels;

/// <summary>
/// Backs the Settings window.
/// </summary>
/// <remarks>
/// Every change saves immediately. There's no Save button. <see cref="HudSettingsChanged"/> tells the app so the HUD
/// rules use the new values on the next layer change.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly IStartupRegistration _startup;
    private HudSettings _hud;
    private StartupState _startupState = new(false, false, null);

    /// <summary>
    /// Creates the view model and loads HUD settings.
    /// </summary>
    /// <remarks>
    /// Call <see cref="LoadStartupAsync"/> afterwards, since the startup state is async.
    /// </remarks>
    /// <param name="store">Settings storage.</param>
    /// <param name="startup">Start at sign-in control.</param>
    public SettingsViewModel(SettingsStore store, IStartupRegistration startup)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(startup);

        _store   = store;
        _startup = startup;
        _hud     = store.Load();
        Layers   = Enumerable.Range(0, 8).Select(i => new LayerOptionViewModel(this, i)).ToArray();
    }

    /// <summary>Raised after HUD settings are saved.</summary>
    public event EventHandler<HudSettings>? HudSettingsChanged;

    // =========================================================================
    // HUD
    // =========================================================================

    /// <summary>Gets or sets the Show HUD switch.</summary>
    public bool HudEnabled
    {
        get => _hud.HudEnabled;
        set
        {
            if (value == _hud.HudEnabled)
            {
                return;
            }

            Apply(_hud with { HudEnabled = value });
            OnPropertyChanged();
            foreach (var layer in Layers)
            {
                layer.RaiseEnabledChanged();
            }
        }
    }

    /// <summary>Gets the 8 layer checkboxes, layers 0 through 7.</summary>
    public IReadOnlyList<LayerOptionViewModel> Layers { get; }

    internal bool IsLayerShown(int index) => (_hud.HudSuppressedLayers & (1 << index)) == 0;

    internal void SetLayerShown(int index, bool shown)
    {
        var mask = shown
            ? _hud.HudSuppressedLayers & ~(1 << index)
            : _hud.HudSuppressedLayers | (1 << index);

        Apply(_hud with { HudSuppressedLayers = (byte)mask });
    }

    private void Apply(HudSettings next)
    {
        _hud = next;
        _store.Save(next);
        HudSettingsChanged?.Invoke(this, next);
    }

    // =========================================================================
    // START AT SIGN-IN
    // =========================================================================

    /// <summary>Gets a value indicating whether the app starts at sign-in.</summary>
    public bool StartAtSignIn => _startupState.IsEnabled;

    /// <summary>Gets a value indicating whether the switch can change it.</summary>
    public bool StartupControllable => _startupState.IsControllable;

    /// <summary>Gets why the switch is locked, if it is.</summary>
    public string? StartupNote => _startupState.Note;

    /// <summary>Gets a value indicating whether the note shows.</summary>
    public bool HasStartupNote => _startupState.Note is not null;

    /// <summary>
    /// Reads the startup state.
    /// </summary>
    /// <remarks>
    /// Called when the window opens.
    /// </remarks>
    /// <returns>A task.</returns>
    public async Task LoadStartupAsync() => ApplyStartup(await _startup.GetStateAsync().ConfigureAwait(true));

    /// <summary>
    /// Turns start at sign-in on or off.
    /// </summary>
    /// <remarks>
    /// Does nothing if the value is unchanged, so a two-way binding echo can't loop.
    /// </remarks>
    /// <param name="enabled">The requested value.</param>
    /// <returns>A task.</returns>
    public async Task SetStartAtSignInAsync(bool enabled)
    {
        if (enabled == StartAtSignIn)
        {
            return;
        }

        ApplyStartup(await _startup.SetEnabledAsync(enabled).ConfigureAwait(true));
    }

    private void ApplyStartup(StartupState state)
    {
        _startupState = state;
        OnPropertyChanged(nameof(StartAtSignIn));
        OnPropertyChanged(nameof(StartupControllable));
        OnPropertyChanged(nameof(StartupNote));
        OnPropertyChanged(nameof(HasStartupNote));
    }

    // =========================================================================
    // ABOUT
    // =========================================================================

    /// <summary>Gets the app version, for example "2.0.0".</summary>
    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

/// <summary>
/// One layer's "show HUD for this layer" checkbox.
/// </summary>
/// <remarks>
/// Checked means the layer is not muted. Disabled while Show HUD is off.
/// </remarks>
public sealed class LayerOptionViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    internal LayerOptionViewModel(SettingsViewModel owner, int index)
    {
        _owner = owner;
        Index  = index;
        Label  = $"Layer {index}";
    }

    /// <summary>Gets the layer number.</summary>
    public int Index { get; }

    /// <summary>Gets the checkbox label.</summary>
    public string Label { get; }

    /// <summary>Gets or sets whether the HUD shows for this layer.</summary>
    public bool IsShown
    {
        get => _owner.IsLayerShown(Index);
        set
        {
            if (value == IsShown)
            {
                return;
            }

            _owner.SetLayerShown(Index, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Gets a value indicating whether the checkbox is enabled.</summary>
    public bool IsEnabled => _owner.HudEnabled;

    internal void RaiseEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Layers.slnx`
Expected: all pass.

---

### Task 11: UI Library, App Shell, Tray Icon, and Lifecycle

**Files:**
- Modify: `src/Layers.Core/NativeMethods.txt`, `Layers.slnx`, `tests/Layers.Tests/Layers.Tests.csproj`
- Create: `src/Layers.UI/Layers.UI.csproj`, `src/Layers.UI/TrayIcon.cs`, `src/Layers/Layers.csproj`, `src/Layers/Program.cs`, `src/Layers/App.xaml`, `src/Layers/App.xaml.cs`, `src/Layers/app.manifest`, `src/Layers/Package.appxmanifest`, `src/Layers/Assets/Square150x150Logo.png`, `src/Layers/Assets/Square44x44Logo.png`, `src/Layers/Assets/StoreLogo.png`
- Test: `tests/Layers.Tests/LifecycleTests.cs`

**Interfaces:**
- Consumes: `DeviceService`, `WinRtHidDeviceSource`, `TrayIconRenderer`, `TrayMessageRouter`, `TrayAction`, `TaskbarTheme`, `StatusText`, `IconMath`, `DeviceState`.
- Produces:
  - `Layers.UI.TrayIcon : IDisposable` with ctor `()`, `event EventHandler? MenuRequested`, `event EventHandler? QuitRequested`, `nint Handle`, and `void Update(DeviceState)`.
  - The hidden window class is `LayersMessageWindow`.
  - The `Layers` exe: single instance on key `LayersTrayApp`, and `App.QuitAsync()`.

- [ ] **Step 1: Append to `src/Layers.Core/NativeMethods.txt`**

```
SetDefaultDllDirectories
GetModuleHandle
RegisterClassEx
CreateWindowEx
DestroyWindow
DefWindowProc
RegisterWindowMessage
FindWindow
PostMessage
GetDpiForWindow
GetDpiForMonitor
MonitorFromPoint
GetCursorPos
SetForegroundWindow
GetForegroundWindow
SetWindowPos
GetWindowLongPtr
SetWindowLongPtr
SetLayeredWindowAttributes
DwmSetWindowAttribute
MessageBox
Shell_NotifyIcon
NOTIFYICONDATAW
WNDCLASSEXW
HWND_TOPMOST
DWMWA_WINDOW_CORNER_PREFERENCE
DWM_WINDOW_CORNER_PREFERENCE
```

- [ ] **Step 2: Create `src/Layers.UI/Layers.UI.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- WinUI Class Library -->
    <PropertyGroup>
        <RootNamespace>Layers.UI</RootNamespace>
        <UseWinUI>true</UseWinUI>
        <IsAotCompatible>true</IsAotCompatible>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    </PropertyGroup>

    <!-- Packages -->
    <ItemGroup>
        <PackageReference Include="Microsoft.WindowsAppSDK" />
    </ItemGroup>

    <!-- Projects -->
    <ItemGroup>
        <ProjectReference Include="..\Layers.Core\Layers.Core.csproj" />
    </ItemGroup>

    <!-- Test Access -->
    <ItemGroup>
        <InternalsVisibleTo Include="Layers.UITests" />
    </ItemGroup>
</Project>
```

- [ ] **Step 3: Implement `src/Layers.UI/TrayIcon.cs`**

As in Task 9, adapt to CsWin32's generated signatures if a name differs, and keep the behavior. `szTip` is a CsWin32 fixed-buffer struct. Copy into it with its generated `AsSpan()`, or assign the string if CsWin32 generates an implicit conversion.

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Layers.Core.Logic;
using Layers.Core.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.UI;

/// <summary>
/// The notification-area icon and the hidden window that receives its messages.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 has no stock tray icon, so this uses <c>Shell_NotifyIconW</c> directly. The hidden window
/// (<c>LayersMessageWindow</c>, <c>WS_EX_TOOLWINDOW</c>, never shown) is a normal top-level window, not
/// <c>HWND_MESSAGE</c>, because message-only windows get neither DPI nor broadcast messages. It's created on the UI
/// thread, whose WinUI message loop dispatches its messages.
/// </para>
/// <para>
/// <c>Shell_NotifyIconW</c> can re-enter the window procedure through a cross-process SendMessage, so refreshes are
/// guarded against nesting. No exception may escape the window procedure: that would abort the process.
/// </para>
/// </remarks>
public sealed unsafe class TrayIcon : IDisposable
{
    private const string WindowClass = "LayersMessageWindow";
    private const uint IconId        = 1;

    private static TrayIcon? s_current;

    private readonly HWND _hwnd;
    private readonly uint _taskbarCreated;
    private DestroyIconSafeHandle? _icon;
    private DeviceState _state = DeviceState.Initial;
    private bool _added;
    private bool _refreshing;

    /// <summary>
    /// Creates the hidden window and adds the icon.
    /// </summary>
    /// <remarks>
    /// Must run on the UI thread. Only one instance may exist.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        s_current = this;

        // Register The Hidden Window Class
        var instance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = WindowClass)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize        = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc   = &WindowProc,
                hInstance     = (HINSTANCE)instance.DangerousGetHandle(),
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(in windowClass);

            // Create It
            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, "Layers", WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0, HWND.Null, null, instance, null);
        }

        if (_hwnd.IsNull)
        {
            s_current = null;
            throw new Win32Exception();
        }

        // Explorer Restart Notification
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

        Apply(NOTIFY_ICON_MESSAGE.NIM_ADD);
    }

    /// <summary>Raised when the icon is left- or right-clicked.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when the hidden window gets <c>WM_CLOSE</c>.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Gets the hidden window handle.</summary>
    public nint Handle => _hwnd;

    /// <summary>
    /// Redraws the icon and tooltip for a new device state.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread whenever <c>DeviceService</c> reports a change.
    /// </remarks>
    /// <param name="state">The device state.</param>
    public void Update(DeviceState state)
    {
        _state = state;
        Apply(_added ? NOTIFY_ICON_MESSAGE.NIM_MODIFY : NOTIFY_ICON_MESSAGE.NIM_ADD);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (s_current != this)
        {
            return;
        }

        // Remove The Icon
        var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = IconId };
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);

        // Release Resources
        PInvoke.DestroyWindow(_hwnd);
        _icon?.Dispose();
        _icon     = null;
        s_current = null;
    }

    // =========================================================================
    // ICON
    // =========================================================================

    private void Apply(NOTIFY_ICON_MESSAGE operation)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            // Render For The Taskbar's DPI And Theme
            var size = IconMath.IconSize(TaskbarDpi());
            var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(_state, TaskbarTheme.IsLight(), size), size);

            // Describe The Icon
            var data = new NOTIFYICONDATAW
            {
                cbSize           = (uint)sizeof(NOTIFYICONDATAW),
                hWnd             = _hwnd,
                uID              = IconId,
                uFlags           = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                uCallbackMessage = TrayMessageRouter.TrayCallbackMessage,
                hIcon            = (HICON)icon.DangerousGetHandle(),
            };
            var tip = StatusText.Tooltip(_state);
            tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(data.szTip.AsSpan());

            // Add Or Modify, Falling Back To Add If The Shell Lost It
            var ok = PInvoke.Shell_NotifyIcon(operation, in data);
            if (!ok && operation == NOTIFY_ICON_MESSAGE.NIM_MODIFY)
            {
                ok = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
            }

            _added = ok || _added;

            // Swap Icons
            _icon?.Dispose();
            _icon = icon;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning("tray refresh failed: {0}", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private uint TaskbarDpi()
    {
        // The Taskbar's Own DPI, Then Ours, Then 96
        var taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        var dpi     = taskbar.IsNull ? 0u : PInvoke.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = PInvoke.GetDpiForWindow(_hwnd);
        }

        return dpi == 0 ? 96u : dpi;
    }

    // =========================================================================
    // WINDOW PROCEDURE
    // =========================================================================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            var self = s_current;
            if (self is not null && hwnd == self._hwnd)
            {
                switch (TrayMessageRouter.Route(message, lParam.Value, self._taskbarCreated))
                {
                    case TrayAction.OpenMenu:
                        self.MenuRequested?.Invoke(self, EventArgs.Empty);
                        return new LRESULT(0);
                    case TrayAction.ReAddIcon:
                        self._added = false;
                        self.Apply(NOTIFY_ICON_MESSAGE.NIM_ADD);
                        return new LRESULT(0);
                    case TrayAction.Quit:
                        self.QuitRequested?.Invoke(self, EventArgs.Empty);
                        return new LRESULT(0);
                    case TrayAction.Refresh:
                        self.Apply(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
                        break;
                    case TrayAction.None:
                    default:
                        break;
                }
            }
        }
#pragma warning disable CA1031 // An exception escaping an unmanaged callback aborts the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError("tray window procedure failed: {0}", ex);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
```

- [ ] **Step 4: Create `src/Layers/Layers.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- WinUI App -->
    <PropertyGroup>
        <OutputType>WinExe</OutputType>
        <RootNamespace>Layers</RootNamespace>
        <AssemblyName>Layers</AssemblyName>
        <UseWinUI>true</UseWinUI>
        <ApplicationManifest>app.manifest</ApplicationManifest>
        <ApplicationIcon>..\..\assets\app.ico</ApplicationIcon>
        <IsAotCompatible>true</IsAotCompatible>
        <PublishAot>true</PublishAot>
        <SelfContained>true</SelfContained>
        <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
    </PropertyGroup>

    <!-- Packaging: unpackaged by default, MSIX with -p:WindowsPackageType=MSIX -->
    <PropertyGroup>
        <EnableMsixTooling>true</EnableMsixTooling>
        <WindowsPackageType Condition="'$(WindowsPackageType)' == ''">None</WindowsPackageType>
        <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
        <AppxPackageSigningEnabled>false</AppxPackageSigningEnabled>
    </PropertyGroup>

    <!-- Packages -->
    <ItemGroup>
        <PackageReference Include="Microsoft.WindowsAppSDK" />
    </ItemGroup>

    <!-- Projects -->
    <ItemGroup>
        <ProjectReference Include="..\Layers.Core\Layers.Core.csproj" />
        <ProjectReference Include="..\Layers.UI\Layers.UI.csproj" />
    </ItemGroup>

    <!-- Manifests And Assets -->
    <ItemGroup>
        <Manifest Include="$(ApplicationManifest)" />
        <Content Include="..\..\assets\app.ico" Link="app.ico" CopyToOutputDirectory="PreserveNewest" />
        <Content Include="Assets\*.png" />
    </ItemGroup>
</Project>
```

- [ ] **Step 5: Create `src/Layers/app.manifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
    <!-- Identity -->
    <assemblyIdentity version="1.0.0.0" name="Layers.app" />

    <!-- Never Elevate -->
    <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
        <security>
            <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                <requestedExecutionLevel level="asInvoker" uiAccess="false" />
            </requestedPrivileges>
        </security>
    </trustInfo>

    <!-- Windows 10 And 11 -->
    <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
        <application>
            <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
        </application>
    </compatibility>

    <!-- Per-Monitor V2 DPI -->
    <application xmlns="urn:schemas-microsoft-com:asm.v3">
        <windowsSettings>
            <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
        </windowsSettings>
    </application>
</assembly>
```

- [ ] **Step 6: Create `src/Layers/Package.appxmanifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
    xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
    xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
    xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
    xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
    IgnorableNamespaces="uap uap5 rescap">

    <!-- Identity -->
    <!-- TODO: Publisher must equal the signing certificate's subject exactly; blocked until the owner supplies the certificate -->
    <Identity
        Name="Artistro08.Layers"
        Publisher="CN=Devin Green"
        Version="2.0.0.0" />

    <!-- Store Properties -->
    <Properties>
        <DisplayName>Layers</DisplayName>
        <PublisherDisplayName>Devin Green</PublisherDisplayName>
        <Logo>Assets\StoreLogo.png</Logo>
    </Properties>

    <!-- Target -->
    <Dependencies>
        <TargetDeviceFamily
            Name="Windows.Desktop"
            MinVersion="10.0.19041.0"
            MaxVersionTested="10.0.26100.0" />
    </Dependencies>

    <!-- Resources -->
    <Resources>
        <Resource Language="x-generate" />
    </Resources>

    <!-- Application -->
    <Applications>
        <Application
            Id="App"
            Executable="$targetnametoken$.exe"
            EntryPoint="$targetentrypoint$">
            <uap:VisualElements
                DisplayName="Layers"
                Description="Tray indicator for the active HID Remapper layer"
                BackgroundColor="transparent"
                Square150x150Logo="Assets\Square150x150Logo.png"
                Square44x44Logo="Assets\Square44x44Logo.png" />

            <!-- Start At Sign-In -->
            <Extensions>
                <uap5:Extension Category="windows.startupTask">
                    <uap5:StartupTask
                        TaskId="LayersStartup"
                        Enabled="true"
                        DisplayName="Layers" />
                </uap5:Extension>
            </Extensions>
        </Application>
    </Applications>

    <!-- Capabilities: full trust and the two HID Remapper collections, nothing else -->
    <Capabilities>
        <rescap:Capability Name="runFullTrust" />
        <DeviceCapability Name="humaninterfacedevice">
            <Device Id="any">
                <Function Type="usage:FF00 0020" />
                <Function Type="usage:FF00 0021" />
            </Device>
        </DeviceCapability>
    </Capabilities>
</Package>
```

The `Description` above is new text. It isn't copied from anywhere, so list it in your report for the owner to approve.

- [ ] **Step 7: Generate the MSIX logos from `assets/icon.png`**

Run in Windows PowerShell 5.1 (it has System.Drawing):

```powershell
powershell -NoProfile -Command {
    Add-Type -AssemblyName System.Drawing
    $source = [System.Drawing.Image]::FromFile("D:\layers\assets\icon.png")
    foreach ($item in @(@("Square150x150Logo", 150), @("Square44x44Logo", 44), @("StoreLogo", 50))) {
        $bitmap   = New-Object System.Drawing.Bitmap $item[1], $item[1]
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = "HighQualityBicubic"
        $graphics.DrawImage($source, 0, 0, $item[1], $item[1])
        $bitmap.Save("D:\layers\src\Layers\Assets\$($item[0]).png", [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
    }
    $source.Dispose()
}
```

Create `src/Layers/Assets/` first.

- [ ] **Step 8: Create `src/Layers/App.xaml`**

```xml
<Application
    x:Class="Layers.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Stock Fluent Styles -->
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 9: Create `src/Layers/Program.cs`**

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;
using Windows.Win32.System.LibraryLoader;

namespace Layers;

/// <summary>
/// Entry point.
/// </summary>
/// <remarks>
/// Replaces the XAML-generated <c>Main</c> so the single-instance check runs before any UI exists. A second launch
/// exits silently, as in Layers 1.0.3.
/// </remarks>
internal static class Program
{
    private const string InstanceKey = "LayersTrayApp";

    [STAThread]
    private static int Main()
    {
        // Keep The Current Directory And PATH Out Of DLL Search
        PInvoke.SetDefaultDllDirectories(LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

        // Single Instance
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            return 0;
        }

        // Run The App
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
```

- [ ] **Step 10: Create `src/Layers/App.xaml.cs`** (Tasks 12–14 extend it)

```csharp
using System.Diagnostics;
using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers;

/// <summary>
/// Composes the app: device link, tray icon, and (in later tasks) menu, HUD, and settings.
/// </summary>
/// <remarks>
/// Only Quit (or <c>WM_CLOSE</c> from the installer) ends the process. Closing a window never does, because
/// <see cref="DispatcherShutdownMode"/> is explicit.
/// </remarks>
internal sealed partial class App : Application
{
    private DispatcherQueue _dispatcher = null!;
    private TrayIcon? _tray;
    private DeviceService? _device;
    private bool _quitting;

    /// <summary>Creates the app.</summary>
    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException    += (_, e) => Trace.TraceError("unhandled: {0}", e.Exception);
    }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // UI Thread Services
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _tray       = new TrayIcon();
            _tray.QuitRequested += (_, _) => _ = QuitAsync();

            // Device Link
            _device = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
            _device.StateChanged += (_, state) => _dispatcher.TryEnqueue(() => OnDeviceState(state));
            _device.Start();
        }
#pragma warning disable CA1031 // Any startup failure is shown to the user, then the app exits
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PInvoke.MessageBox(HWND.Null, ex.Message, "Layers", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);
            Exit();
        }
    }

    private void OnDeviceState(DeviceState state)
    {
        _tray?.Update(state);
    }

    /// <summary>
    /// Shuts down cleanly.
    /// </summary>
    /// <remarks>
    /// Turns Monitor mode off, removes the tray icon, then exits. Safe to call more than once.
    /// </remarks>
    /// <returns>A task.</returns>
    internal async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_device is not null)
        {
            await _device.DisposeAsync();
        }

        _tray?.Dispose();
        Exit();
    }
}
```

- [ ] **Step 11: Add the projects to the solution, and make the test project build the app**

```bash
dotnet sln Layers.slnx add src/Layers.UI/Layers.UI.csproj src/Layers/Layers.csproj
```

In `tests/Layers.Tests/Layers.Tests.csproj`, add to the Projects `ItemGroup`:

```xml
<ProjectReference Include="..\..\src\Layers\Layers.csproj" ReferenceOutputAssembly="false" />
```

- [ ] **Step 12: Write the failing lifecycle tests** `tests/Layers.Tests/LifecycleTests.cs`

```csharp
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Layers.Tests;

/// Launches the real Layers.exe. It touches the real tray and, if one is plugged in, the real device, exactly as the
/// app does in normal use.
[TestClass]
[DoNotParallelize]
public sealed class LifecycleTests
{
    private readonly List<Process> _started = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var process in _started.Where(p => !p.HasExited))
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }

    private static string ExePath()
    {
        // Walk Up To The Repo Root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "repo root not found");

        // Newest Built Exe
        return Directory.GetFiles(Path.Combine(dir.FullName, "src", "Layers", "bin"), "Layers.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private Process Launch()
    {
        var process = Process.Start(new ProcessStartInfo(ExePath()) { UseShellExecute = false })!;
        _started.Add(process);
        return process;
    }

    private static async Task<HWND> WaitForTrayWindowAsync()
    {
        for (var i = 0; i < 150; i++)
        {
            var hwnd = PInvoke.FindWindow("LayersMessageWindow", null);
            if (!hwnd.IsNull)
            {
                return hwnd;
            }

            await Task.Delay(100);
        }

        Assert.Fail("tray window never appeared");
        return HWND.Null;
    }

    [TestInitialize]
    public void RequireNoRunningLayers()
    {
        if (Process.GetProcessesByName("Layers").Length > 0)
        {
            Assert.Inconclusive("Layers is already running; quit it to run lifecycle tests");
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Starts_AndCreatesTrayWindow()
    {
        var first = Launch();

        await WaitForTrayWindowAsync();

        Assert.IsFalse(first.HasExited);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SecondInstance_ExitsSilently()
    {
        var first = Launch();
        await WaitForTrayWindowAsync();

        var second = Launch();
        var exited = second.WaitForExit(15_000);

        Assert.IsTrue(exited, "second instance kept running");
        Assert.AreEqual(0, second.ExitCode);
        Assert.IsFalse(first.HasExited);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task WmClose_QuitsCleanly()
    {
        var first = Launch();
        var hwnd  = await WaitForTrayWindowAsync();

        PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);

        Assert.IsTrue(first.WaitForExit(15_000), "app did not quit on WM_CLOSE");
        Assert.AreEqual(0, first.ExitCode);
        Assert.IsTrue(PInvoke.FindWindow("LayersMessageWindow", null).IsNull, "tray window left behind");
    }
}
```

- [ ] **Step 13: Run the tests and fix until they pass**

Run: `dotnet build Layers.slnx` and then `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: build succeeds with zero warnings, and all tests pass, including the three lifecycle tests.

If `Application.Start` or activation fails in the unpackaged exe (for example `REGDB_E_CLASSNOTREG` or a missing `Microsoft.ui.xaml.dll`), check that `WindowsAppSDKSelfContained` is true and the output folder has the Windows App SDK DLLs. Report the exact error if you can't fix it.

- [ ] **Step 14: Look at the tray**

Run `src/Layers/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Layers.exe` (or wherever the build put it). Take a screenshot of the taskbar's notification area with PowerShell:

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$env:TEMP\layers-tray.png")
```

Read the PNG. Confirm the layers glyph is in the tray (it may sit in the overflow flyout). Then quit the app with `taskkill /IM Layers.exe` (this sends `WM_CLOSE`, not `/F`).

---

### Task 12: Tray Menu

**Files:**
- Create: `src/Layers.UI/TrayMenuHost.xaml`, `src/Layers.UI/TrayMenuHost.xaml.cs`
- Modify: `src/Layers/App.xaml.cs`

**Interfaces:**
- Consumes: `TrayMenuViewModel`, `StatusTone`, `TrayIcon.MenuRequested`.
- Produces: `Layers.UI.TrayMenuHost : Window` with:
  - ctor `(TrayMenuViewModel model)`
  - `TrayMenuViewModel Model`
  - `event EventHandler? SettingsRequested` and `event EventHandler? QuitRequested`
  - `void ShowAtCursor()` and `bool IsMenuOpen`
  - `static Brush ToneBrush(StatusTone)`
  - `internal IReadOnlyList<MenuFlyoutItemBase> MenuItems` and `internal void RefreshBindings()` (for UI tests)

- [ ] **Step 1: Create `src/Layers.UI/TrayMenuHost.xaml`**

```xml
<Window
    x:Class="Layers.UI.TrayMenuHost"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:Layers.UI"
    Title="Layers">

    <!-- Invisible Anchor For The Menu -->
    <Grid
        x:Name="Root"
        Background="Transparent">
        <FlyoutBase.AttachedFlyout>
            <MenuFlyout
                x:Name="Menu"
                Closed="Menu_Closed">

                <!-- Status Row -->
                <MenuFlyoutItem
                    x:Name="StatusItem"
                    Text="{x:Bind Model.StatusLabel, Mode=OneWay}"
                    IsEnabled="False">
                    <MenuFlyoutItem.Icon>
                        <PathIcon
                            Data="M 4,8 A 4,4 0 1 1 12,8 A 4,4 0 1 1 4,8 Z"
                            Foreground="{x:Bind local:TrayMenuHost.ToneBrush(Model.Tone), Mode=OneWay}" />
                    </MenuFlyoutItem.Icon>
                </MenuFlyoutItem>

                <!-- Status Detail Row -->
                <MenuFlyoutItem
                    x:Name="DetailItem"
                    Text="{x:Bind Model.StatusDetail, Mode=OneWay}"
                    Visibility="{x:Bind Model.HasStatusDetail, Mode=OneWay}"
                    IsEnabled="False" />

                <!-- Layer Row -->
                <MenuFlyoutItem
                    x:Name="LayerItem"
                    Text="{x:Bind Model.LayerLabel, Mode=OneWay}"
                    IsEnabled="False">
                    <MenuFlyoutItem.Icon>
                        <FontIcon Glyph="&#xE81E;" />
                    </MenuFlyoutItem.Icon>
                </MenuFlyoutItem>

                <MenuFlyoutSeparator />

                <!-- Settings -->
                <MenuFlyoutItem
                    x:Name="SettingsItem"
                    Text="Settings…"
                    Click="Settings_Click">
                    <MenuFlyoutItem.Icon>
                        <FontIcon Glyph="&#xE713;" />
                    </MenuFlyoutItem.Icon>
                </MenuFlyoutItem>

                <MenuFlyoutSeparator />

                <!-- Quit -->
                <MenuFlyoutItem
                    x:Name="QuitItem"
                    Text="Quit"
                    Click="Quit_Click">
                    <MenuFlyoutItem.Icon>
                        <FontIcon Glyph="&#xE7E8;" />
                    </MenuFlyoutItem.Icon>
                </MenuFlyoutItem>
            </MenuFlyout>
        </FlyoutBase.AttachedFlyout>
    </Grid>
</Window>
```

If `Glyph="&#xE81E;"` was changed in Task 9 Step 7, use the same code point here.

- [ ] **Step 2: Create `src/Layers.UI/TrayMenuHost.xaml.cs`**

```csharp
using Layers.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// Hosts the stock tray menu.
/// </summary>
/// <remarks>
/// A stock <see cref="MenuFlyout"/> needs a XAML root to open from, so this is a 1×1 borderless window that never
/// appears in the taskbar or Alt+Tab. On open it moves to the cursor, takes the foreground (required for light
/// dismiss), and shows the menu. It hides again when the menu closes. Theme, DPI, Esc, outside-click dismissal,
/// keyboard, and screen reader support all come from the stock control.
/// </remarks>
public sealed partial class TrayMenuHost : Window
{
    private readonly HWND _hwnd;

    /// <summary>
    /// Creates the host.
    /// </summary>
    /// <remarks>
    /// The menu binds to <paramref name="model"/>, so an open menu updates live.
    /// </remarks>
    /// <param name="model">The menu's view model.</param>
    public TrayMenuHost(TrayMenuViewModel model)
    {
        Model = model;
        InitializeComponent();
        _hwnd = new HWND(WindowNative.GetWindowHandle(this));

        // Tiny, Borderless, Hidden From Switchers
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable    = false;
        presenter.IsMaximizable  = false;
        presenter.IsMinimizable  = false;
        presenter.IsAlwaysOnTop  = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new SizeInt32(1, 1));
    }

    /// <summary>Raised when Settings… is clicked.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when Quit is clicked.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Gets the menu's view model.</summary>
    public TrayMenuViewModel Model { get; }

    /// <summary>Gets a value indicating whether the menu is open.</summary>
    public bool IsMenuOpen => Menu.IsOpen;

    internal IReadOnlyList<MenuFlyoutItemBase> MenuItems => Menu.Items.ToList();

    /// <summary>
    /// Maps a status tone to a stock theme brush.
    /// </summary>
    /// <remarks>
    /// Used by x:Bind for the status dot.
    /// </remarks>
    /// <param name="tone">The tone.</param>
    /// <returns>The brush.</returns>
    public static Brush ToneBrush(StatusTone tone) => (Brush)Application.Current.Resources[tone switch
    {
        StatusTone.Success => "SystemFillColorSuccessBrush",
        StatusTone.Caution => "SystemFillColorCautionBrush",
        _                  => "SystemFillColorCriticalBrush",
    }];

    /// <summary>
    /// Opens the menu at the cursor.
    /// </summary>
    /// <remarks>
    /// Called from the tray icon's click handler, which is the moment Windows allows taking the foreground.
    /// </remarks>
    public void ShowAtCursor()
    {
        // Move To The Cursor
        PInvoke.GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(cursor.X, cursor.Y));

        // Take The Foreground So Light Dismiss Works
        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);

        // Open Above The Cursor, Flipping If Needed
        Menu.ShowAt(Root, new FlyoutShowOptions { Position = new Point(0, 0), Placement = FlyoutPlacementMode.TopEdgeAlignedLeft });
    }

    internal void RefreshBindings() => Bindings.Update();

    private void Menu_Closed(object sender, object e) => AppWindow.Hide();

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 3: Wire the menu into `src/Layers/App.xaml.cs`**

Add these fields:

```csharp
    private readonly TrayMenuViewModel _menuModel = new();
    private TrayMenuHost? _menu;
```

Add `using Layers.Core.ViewModels;`. In `OnLaunched`, after the tray is created:

```csharp
            // Tray Menu
            _menu = new TrayMenuHost(_menuModel);
            _menu.QuitRequested += (_, _) => _ = QuitAsync();
            _tray.MenuRequested += (_, _) => _menu.ShowAtCursor();
```

Replace `OnDeviceState` with:

```csharp
    private void OnDeviceState(DeviceState state)
    {
        _menuModel.State = state;
        _tray?.Update(state);
    }
```

In `QuitAsync`, before `_tray?.Dispose();`, add `_menu?.Close();`.

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build Layers.slnx` and then `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: zero warnings, all tests pass. The UI tests for this window come in Task 15.

- [ ] **Step 5: Look at the menu**

Run the app, then:
- Open the menu with a synthetic click: post `WM_APP+1` with `lParam = WM_LBUTTONUP (0x0202)` to `LayersMessageWindow`, using a throwaway PowerShell script with P/Invoke.
- Take a screenshot as in Task 11 Step 14 and read it.

Confirm:
1. The menu shows a red dot and "Disconnected", then "Layer 0", "Settings…", and "Quit".
2. It sits above the cursor.
3. Esc closes it.

Then click Quit with a synthetic click, or send `taskkill /IM Layers.exe`. Report what you saw.

---

### Task 13: HUD Window

**Files:**
- Create: `src/Layers.UI/WindowStyles.cs`, `src/Layers.UI/HudWindow.xaml`, `src/Layers.UI/HudWindow.xaml.cs`
- Modify: `src/Layers/App.xaml.cs`

**Interfaces:**
- Consumes: `HudTiming`, `HudPlacement`, `HudRules`, `HudSettings`, `SettingsStore`, `LayerMask`.
- Produces:
  - `static class WindowStyles` with:
    - `void MakeToolOverlay(nint hwnd, bool clickThrough)`
    - `uint GetExtendedStyle(nint hwnd)`
    - `void SetOpacity(nint hwnd, double opacity)`
    - `void BringToTopmost(nint hwnd)`
    - `void RoundCorners(nint hwnd)`
    - `double PrimaryScale()` and `double ScaleAt(Windows.Graphics.PointInt32 point)`
  - `Layers.UI.HudWindow : Window` with ctor `()`, `void Show(LayerMask layers)`, `string Text`, and `nint Handle`.

- [ ] **Step 1: Create `src/Layers.UI/WindowStyles.cs`**

```csharp
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.UI;

/// <summary>
/// Win32 window tweaks WinUI has no stock API for.
/// </summary>
/// <remarks>
/// Click-through, no-activate, per-window alpha, re-asserting topmost, rounded corners, and monitor scale.
/// </remarks>
public static unsafe class WindowStyles
{
    /// <summary>
    /// Makes a window a tool overlay.
    /// </summary>
    /// <remarks>
    /// Adds <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED</c>, plus <c>WS_EX_TRANSPARENT</c> when
    /// <paramref name="clickThrough"/> is set, so mouse input passes to whatever is underneath.
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    /// <param name="clickThrough">Let clicks pass through.</param>
    public static void MakeToolOverlay(nint hwnd, bool clickThrough)
    {
        var style = (WINDOW_EX_STYLE)GetExtendedStyle(hwnd)
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_LAYERED;
        if (clickThrough)
        {
            style |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        }

        PInvoke.SetWindowLongPtr(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)style);
        SetOpacity(hwnd, 1.0);
    }

    /// <summary>Reads the extended style.</summary>
    /// <remarks>Used by UI tests to verify the overlay styles.</remarks>
    /// <param name="hwnd">The window.</param>
    /// <returns>The <c>WS_EX_*</c> bits.</returns>
    public static uint GetExtendedStyle(nint hwnd) =>
        (uint)PInvoke.GetWindowLongPtr(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

    /// <summary>Sets whole-window opacity.</summary>
    /// <remarks>Fades the backdrop and content together. Requires <c>WS_EX_LAYERED</c>.</remarks>
    /// <param name="hwnd">The window.</param>
    /// <param name="opacity">0 to 1.</param>
    public static void SetOpacity(nint hwnd, double opacity) =>
        PInvoke.SetLayeredWindowAttributes(new HWND(hwnd), new COLORREF(0), (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

    /// <summary>Moves a window to the front of the topmost band without activating it.</summary>
    /// <remarks>
    /// <c>WS_EX_TOPMOST</c> only puts a window in the topmost band. Order within the band follows activation, so the
    /// HUD re-asserts this on every show.
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    public static void BringToTopmost(nint hwnd) =>
        PInvoke.SetWindowPos(new HWND(hwnd), HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>Asks DWM for Windows 11 rounded corners.</summary>
    /// <remarks>Ignored on Windows 10.</remarks>
    /// <param name="hwnd">The window.</param>
    public static void RoundCorners(nint hwnd)
    {
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    /// <summary>Gets the primary monitor's scale.</summary>
    /// <remarks>The HUD always sits on the primary monitor.</remarks>
    /// <returns>For example 1.5 at 144 DPI.</returns>
    public static double PrimaryScale() => ScaleAt(new PointInt32(0, 0), primary: true);

    /// <summary>Gets the scale of the monitor containing a point.</summary>
    /// <remarks>Used to size the Settings window on the monitor under the cursor.</remarks>
    /// <param name="point">A screen point.</param>
    /// <returns>The scale.</returns>
    public static double ScaleAt(PointInt32 point) => ScaleAt(point, primary: false);

    private static double ScaleAt(PointInt32 point, bool primary)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y),
            primary ? MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY : MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded
            ? dpiX / 96.0
            : 1.0;
    }
}
```

- [ ] **Step 2: Create `src/Layers.UI/HudWindow.xaml`**

```xml
<Window
    x:Class="Layers.UI.HudWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Layers HUD">

    <!-- HUD Content -->
    <Grid
        x:Name="Root"
        Padding="14,0">
        <StackPanel
            Orientation="Horizontal"
            Spacing="10"
            HorizontalAlignment="Center"
            VerticalAlignment="Center">
            <FontIcon
                Glyph="&#xE81E;"
                FontSize="20" />
            <TextBlock
                x:Name="Label"
                FontSize="16"
                VerticalAlignment="Center" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Create `src/Layers.UI/HudWindow.xaml.cs`**

```csharp
using System.Diagnostics;
using Layers.Core.Logic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// The layer-change HUD.
/// </summary>
/// <remarks>
/// A borderless acrylic window at the bottom center of the primary monitor's work area. It's click-through, never takes
/// focus, and stays in front of the topmost band. It holds for 550 ms, then fades over 200 ms. A new change during the
/// hold or fade restarts at full opacity. The fade uses layered-window alpha so the acrylic backdrop fades with the
/// content.
/// </remarks>
public sealed partial class HudWindow : Window
{
    private readonly nint _hwnd;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = new();

    /// <summary>
    /// Creates the hidden HUD.
    /// </summary>
    /// <remarks>
    /// Created once at startup and reused for every change.
    /// </remarks>
    public HudWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Borderless Acrylic Overlay
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter  = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        WindowStyles.MakeToolOverlay(_hwnd, clickThrough: true);
        WindowStyles.RoundCorners(_hwnd);

        // Fade Timer
        _timer          = DispatcherQueue.CreateTimer();
        _timer.Interval = HudTiming.Tick;
        _timer.Tick    += OnTick;
    }

    /// <summary>Gets the shown label.</summary>
    public string Text => Label.Text;

    /// <summary>Gets the window handle.</summary>
    public nint Handle => _hwnd;

    /// <summary>
    /// Shows the HUD for a layer state.
    /// </summary>
    /// <remarks>
    /// Resizes to fit the label, re-centers on the primary work area, and restarts the hold-and-fade.
    /// </remarks>
    /// <param name="layers">The new layers.</param>
    public void Show(LayerMask layers)
    {
        // Content
        Label.Text = layers.Label;
        Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Size And Position On The Primary Monitor
        var scale = WindowStyles.PrimaryScale();
        var size  = HudPlacement.Size(Label.DesiredSize.Width, scale);
        var point = HudPlacement.Place(DisplayArea.Primary.WorkArea, size, scale);
        AppWindow.MoveAndResize(new RectInt32(point.X, point.Y, size.Width, size.Height));

        // Show Without Focus, In Front, Fully Opaque
        WindowStyles.SetOpacity(_hwnd, 1.0);
        AppWindow.Show(false);
        WindowStyles.BringToTopmost(_hwnd);

        // Restart The Hold And Fade
        _clock.Restart();
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = _clock.Elapsed;
        WindowStyles.SetOpacity(_hwnd, HudTiming.OpacityAt(elapsed));

        if (HudTiming.IsFinished(elapsed))
        {
            _timer.Stop();
            AppWindow.Hide();
        }
    }
}
```

- [ ] **Step 4: Wire the HUD into `src/Layers/App.xaml.cs`**

Add fields:

```csharp
    private readonly SettingsStore _store = new();
    private HudSettings _hudSettings;
    private DeviceState _lastState = DeviceState.Initial;
    private HudWindow? _hud;
```

In `OnLaunched`, before the tray is created:

```csharp
            // Settings And HUD
            _hudSettings = _store.Load();
            _hud         = new HudWindow();
```

Replace `OnDeviceState` with:

```csharp
    private void OnDeviceState(DeviceState state)
    {
        // Decide On The HUD Before Updating
        var previous = _lastState;
        _lastState   = state;
        if (HudRules.ShouldShow(_hudSettings, previous, state))
        {
            _hud?.Show(state.Layers);
        }

        // Update Tray And Menu
        _menuModel.State = state;
        _tray?.Update(state);
    }
```

In `QuitAsync`, before `_tray?.Dispose();`, add `_hud?.Close();`.

- [ ] **Step 5: Probe the HUD (the spec's second open question)**

Write a throwaway console harness in the scratchpad, or temporarily call `_hud.Show(new LayerMask(0b100))` from `OnLaunched` (remove it afterwards). Take screenshots at about 100 ms, 650 ms, and 900 ms after show (a PowerShell loop around the screenshot snippet from Task 11 Step 14). Read them and report:
1. Is the acrylic backdrop visible at 100 ms?
2. Is the HUD partly faded at 650 ms, and gone at 900 ms?
3. Does a click on the HUD's area reach the window underneath? Test by showing the HUD over a Notepad window and posting a mouse click with `SendInput` from the harness.

If clicks don't pass through, the WinUI content child window is catching them. Walk the HUD's child windows (`EnumChildWindows`) and add `WS_EX_TRANSPARENT | WS_EX_LAYERED` to each, inside `WindowStyles.MakeToolOverlay`. If acrylic doesn't render with `WS_EX_LAYERED`, apply the spec's Fallback 1: remove `SystemBackdrop`, and set `Root.Background` to `{ThemeResource AcrylicBackgroundFillColorDefaultBrush}`. If `LWA_ALPHA` has no visible effect, apply Fallback 2: animate `Root.Opacity` with `HudTiming.OpacityAt` in `OnTick` instead of `SetOpacity`. Report which path was taken.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build Layers.slnx` and then `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: zero warnings, all pass.

---

### Task 14: Settings Window and Final App Wiring

**Files:**
- Create: `src/Layers.UI/SettingsWindow.xaml`, `src/Layers.UI/SettingsWindow.xaml.cs`
- Modify: `src/Layers/App.xaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel`, `LayerOptionViewModel`, `IStartupRegistration`, `RunKeyStartup`, `PackagedStartupTask`, `AppPackaging`, `WindowStyles.ScaleAt`.
- Produces: `Layers.UI.SettingsWindow : Window` with:
  - `static SettingsWindow Open(Func<SettingsViewModel> createModel)`, which activates the existing window or creates one
  - `static SettingsWindow? Current`
  - `SettingsViewModel Model`
  - `internal IEnumerable<Control> Controls()` (for UI tests)

- [ ] **Step 1: Create `src/Layers.UI/SettingsWindow.xaml`**

```xml
<Window
    x:Class="Layers.UI.SettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:Layers.Core.ViewModels"
    Title="Layers Settings">

    <Grid x:Name="Root">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Title Bar -->
        <TextBlock
            x:Name="TitleBarText"
            Grid.Row="0"
            Margin="16,12,0,8"
            Style="{StaticResource CaptionTextBlockStyle}"
            Text="Layers Settings" />

        <!-- Content -->
        <ScrollViewer Grid.Row="1">
            <StackPanel
                Padding="24,8,24,24"
                Spacing="24">

                <!-- General -->
                <StackPanel Spacing="8">
                    <TextBlock
                        Style="{StaticResource BodyStrongTextBlockStyle}"
                        Text="General" />
                    <ToggleSwitch
                        x:Name="StartupSwitch"
                        Header="Start at sign-in"
                        IsOn="{x:Bind Model.StartAtSignIn, Mode=OneWay}"
                        IsEnabled="{x:Bind Model.StartupControllable, Mode=OneWay}"
                        Toggled="StartupSwitch_Toggled"
                        AutomationProperties.Name="Start at sign-in" />
                    <TextBlock
                        x:Name="StartupNote"
                        Text="{x:Bind Model.StartupNote, Mode=OneWay}"
                        Visibility="{x:Bind Model.HasStartupNote, Mode=OneWay}"
                        Style="{StaticResource CaptionTextBlockStyle}"
                        Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                        TextWrapping="Wrap" />
                </StackPanel>

                <!-- HUD -->
                <StackPanel Spacing="8">
                    <TextBlock
                        Style="{StaticResource BodyStrongTextBlockStyle}"
                        Text="HUD" />
                    <ToggleSwitch
                        x:Name="HudSwitch"
                        Header="Show HUD"
                        IsOn="{x:Bind Model.HudEnabled, Mode=TwoWay}"
                        AutomationProperties.Name="Show HUD" />
                    <TextBlock Text="Show for these layers" />

                    <!-- Layer Checkboxes -->
                    <ItemsControl ItemsSource="{x:Bind Model.Layers}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:LayerOptionViewModel">
                                <CheckBox
                                    Content="{x:Bind Label}"
                                    IsChecked="{x:Bind IsShown, Mode=TwoWay}"
                                    IsEnabled="{x:Bind IsEnabled, Mode=OneWay}"
                                    AutomationProperties.Name="{x:Bind Label}" />
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <!-- /Layer Checkboxes -->
                </StackPanel>

                <!-- About -->
                <StackPanel Spacing="4">
                    <TextBlock
                        Style="{StaticResource BodyStrongTextBlockStyle}"
                        Text="About" />
                    <TextBlock Text="{x:Bind Model.Version}" />
                    <HyperlinkButton
                        Content="GitHub"
                        NavigateUri="https://github.com/artistro08/layers"
                        AutomationProperties.Name="GitHub" />
                </StackPanel>
            </StackPanel>
        </ScrollViewer>
        <!-- /Content -->
    </Grid>
</Window>
```

"GitHub" and the bare version number are the About texts the spec implies ("the version and a GitHub link"). List them in your report for the owner to confirm.

- [ ] **Step 2: Create `src/Layers.UI/SettingsWindow.xaml.cs`**

```csharp
using Layers.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Win32;

namespace Layers.UI;

/// <summary>
/// The Settings window.
/// </summary>
/// <remarks>
/// Stock controls on a Mica backdrop with the content extended into the title bar. Only one exists at a time:
/// <see cref="Open"/> activates it if it's already open. Closing destroys it to free memory. The app keeps running.
/// Every change saves immediately through the view model.
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private const int LogicalWidth  = 460;
    private const int LogicalHeight = 560;

    private SettingsWindow(SettingsViewModel model)
    {
        Model = model;
        InitializeComponent();

        // Mica And Custom Title Bar
        SystemBackdrop            = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarText);

        // Fixed Size
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);

        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        Closed += (_, _) => Current = null;
    }

    /// <summary>Gets the open window, if any.</summary>
    public static SettingsWindow? Current { get; private set; }

    /// <summary>Gets the view model.</summary>
    public SettingsViewModel Model { get; }

    /// <summary>
    /// Opens Settings, or brings the open window to the front.
    /// </summary>
    /// <remarks>
    /// A new window is centered on the monitor under the cursor and sized for that monitor's scale.
    /// </remarks>
    /// <param name="createModel">Creates the view model for a new window.</param>
    /// <returns>The window.</returns>
    public static SettingsWindow Open(Func<SettingsViewModel> createModel)
    {
        ArgumentNullException.ThrowIfNull(createModel);

        // Reuse The Open Window
        if (Current is not null)
        {
            Current.Activate();
            return Current;
        }

        // Create, Size, And Center Under The Cursor
        var window = new SettingsWindow(createModel());
        PInvoke.GetCursorPos(out var cursor);
        var point  = new PointInt32(cursor.X, cursor.Y);
        var scale  = WindowStyles.ScaleAt(point);
        var width  = (int)Math.Round(LogicalWidth * scale);
        var height = (int)Math.Round(LogicalHeight * scale);
        var work   = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Current = window;
        window.Activate();
        _ = window.Model.LoadStartupAsync();
        return window;
    }

    internal IEnumerable<Control> Controls() => Descendants(Root).OfType<Control>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e) =>
        await Model.SetStartAtSignInAsync(StartupSwitch.IsOn);
}
```

- [ ] **Step 3: Finish `src/Layers/App.xaml.cs`**

Add a field:

```csharp
    private IStartupRegistration _startup = null!;
```

In `OnLaunched`, after `_hudSettings = _store.Load();`:

```csharp
            // Start At Sign-In, Per Packaging
            _startup = AppPackaging.IsPackaged
                ? new PackagedStartupTask()
                : new RunKeyStartup(Environment.ProcessPath!);
```

After the menu is created:

```csharp
            _menu.SettingsRequested += (_, _) => OpenSettings();
```

Add the method:

```csharp
    private void OpenSettings()
    {
        SettingsWindow.Open(() =>
        {
            var model = new SettingsViewModel(_store, _startup);
            model.HudSettingsChanged += (_, settings) => _hudSettings = settings;
            return model;
        });
    }
```

In `QuitAsync`, before `_hud?.Close();`, add `SettingsWindow.Current?.Close();`.

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build Layers.slnx` and then `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: zero warnings, all pass.

- [ ] **Step 5: Look at Settings**

Run the app and open Settings (synthetic tray click, then click "Settings…"). Screenshot it and read the image. Confirm:
- Mica background, "Layers Settings" title, the three sections, and 8 checkboxes.
- Unchecking "Layer 2" updates `HKCU\Software\Layers\HudSuppressedLayers` (`reg query HKCU\Software\Layers`).
- Turning off Show HUD greys out the checkboxes.
- Choosing Settings… again doesn't open a second window.

Don't touch the Start at sign-in switch: it would point the real Run key at the Debug build. Then restore the registry values you changed: `reg add HKCU\Software\Layers /v HudSuppressedLayers /t REG_DWORD /d <original> /f` and the same for `HudEnabled`, with the originals read before you started.

---

### Task 15: UI Tests

**Files:**
- Create: `tests/Layers.UITests/Layers.UITests.csproj`, `tests/Layers.UITests/UiHost.cs`, `tests/Layers.UITests/FakeStartup.cs`, `tests/Layers.UITests/SettingsWindowTests.cs`, `tests/Layers.UITests/HudWindowTests.cs`, `tests/Layers.UITests/TrayMenuTests.cs`
- Modify: `Layers.slnx`

**Interfaces:**
- Consumes: `SettingsWindow`, `HudWindow`, `TrayMenuHost`, `WindowStyles`, `SettingsViewModel`, `TrayMenuViewModel`, `SettingsStore`, `IStartupRegistration`, `HudPlacement`.
- Produces: `UiHost.RunAsync(Func<Task>)`, which runs test code on a live WinUI UI thread.

The UI tests run as their own WinUI exe (MSTest runner mode), so the XAML resources of `Layers.UI` load the same way they do in the app. This replaces `[UITestMethod]`: each test body runs on the UI thread through `UiHost.RunAsync`, which also lets tests `await` layout and timers.

- [ ] **Step 1: Create `tests/Layers.UITests/Layers.UITests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- WinUI Test Exe -->
    <PropertyGroup>
        <RootNamespace>Layers.UITests</RootNamespace>
        <OutputType>Exe</OutputType>
        <UseWinUI>true</UseWinUI>
        <EnableMSTestRunner>true</EnableMSTestRunner>
        <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
        <WindowsPackageType>None</WindowsPackageType>
        <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
        <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        <IsPackable>false</IsPackable>
        <NoWarn>$(NoWarn);CA1707;CS1591;CA1515</NoWarn>
    </PropertyGroup>

    <!-- Packages -->
    <ItemGroup>
        <PackageReference Include="MSTest.TestFramework" />
        <PackageReference Include="MSTest.TestAdapter" />
        <PackageReference Include="Microsoft.WindowsAppSDK" />
    </ItemGroup>

    <!-- Projects -->
    <ItemGroup>
        <ProjectReference Include="..\..\src\Layers.UI\Layers.UI.csproj" />
        <ProjectReference Include="..\..\src\Layers.Core\Layers.Core.csproj" />
    </ItemGroup>

    <!-- Shared Test Helper -->
    <ItemGroup>
        <Compile Include="..\Layers.Tests\TempRegistryKey.cs" Link="TempRegistryKey.cs" />
    </ItemGroup>

    <!-- UI Tests Share One UI Thread -->
    <ItemGroup>
        <AssemblyAttribute Include="Microsoft.VisualStudio.TestTools.UnitTesting.DoNotParallelizeAttribute" />
    </ItemGroup>
</Project>
```

Then run `dotnet sln Layers.slnx add tests/Layers.UITests/Layers.UITests.csproj`.

- [ ] **Step 2: Create `tests/Layers.UITests/UiHost.cs`**

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

/// Starts one WinUI Application on a dedicated STA thread for the whole test run.
[TestClass]
public static class UiHost
{
    private static DispatcherQueue? s_queue;

    [AssemblyInitialize]
    public static void Start(TestContext context)
    {
        using var ready = new ManualResetEventSlim();
        var thread      = new Thread(() =>
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                s_queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(s_queue));
                _ = new TestApp();
                ready.Set();
            });
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WinUI host did not start");
        }
    }

    [AssemblyCleanup]
    public static void Stop() => s_queue?.TryEnqueue(() => Application.Current.Exit());

    /// Runs a test body on the UI thread and surfaces its exceptions to MSTest.
    public static Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = s_queue!.TryEnqueue(async () =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        if (!queued)
        {
            throw new InvalidOperationException("UI thread is gone");
        }

        return done.Task;
    }

    /// Lets layout and bindings settle.
    public static Task Settle() => Task.Delay(300);
}

/// Minimal app with the stock Fluent resources the windows use.
internal sealed partial class TestApp : Application
{
    public TestApp()
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new XamlControlsResources());
    }
}
```

`tests/Layers.UITests/FakeStartup.cs`:

```csharp
using Layers.Core.Services;

namespace Layers.UITests;

internal sealed class FakeStartup(StartupState state) : IStartupRegistration
{
    public Task<StartupState> GetStateAsync() => Task.FromResult(state);

    public Task<StartupState> SetEnabledAsync(bool enabled) => Task.FromResult(state = state with { IsEnabled = enabled });
}
```

- [ ] **Step 3: Write the Settings window tests** `tests/Layers.UITests/SettingsWindowTests.cs`

```csharp
using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;
using Layers.Tests;
using Layers.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Layers.UITests;

[TestClass]
public sealed class SettingsWindowTests
{
    private TempRegistryKey _key = null!;
    private SettingsStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _key   = new TempRegistryKey();
        _store = new SettingsStore(_key.Path);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await UiHost.RunAsync(() =>
        {
            SettingsWindow.Current?.Close();
            return Task.CompletedTask;
        });
        _key.Dispose();
    }

    private SettingsWindow Open(StartupState? startup = null) =>
        SettingsWindow.Open(() => new SettingsViewModel(_store, new FakeStartup(startup ?? new StartupState(true, true, null))));

    [TestMethod]
    public Task Opens_WithStoredValues() => UiHost.RunAsync(async () =>
    {
        _store.Save(new HudSettings(false, 0b100));

        var window = Open();
        await UiHost.Settle();

        var hudSwitch = window.Controls().OfType<ToggleSwitch>().Single(t => (string)t.Header == "Show HUD");
        var boxes     = window.Controls().OfType<CheckBox>().ToList();
        Assert.IsFalse(hudSwitch.IsOn);
        Assert.AreEqual(8, boxes.Count);
        Assert.IsFalse(boxes[2].IsChecked);
        Assert.IsTrue(boxes[1].IsChecked);
        Assert.IsTrue(boxes.All(b => !b.IsEnabled), "checkboxes stay enabled while the HUD is off");
    });

    [TestMethod]
    public Task UncheckingLayer_Saves() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        window.Controls().OfType<CheckBox>().ElementAt(3).IsChecked = false;
        await UiHost.Settle();

        Assert.AreEqual((byte)0b1000, _store.Load().HudSuppressedLayers);
    });

    [TestMethod]
    public Task Open_Twice_ReusesWindow() => UiHost.RunAsync(async () =>
    {
        var first = Open();
        await UiHost.Settle();

        var second = Open();

        Assert.AreSame(first, second);
    });

    [TestMethod]
    public Task EveryControl_HasAutomationName() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var interactive = window.Controls().Where(c => c is ToggleSwitch or CheckBox or ButtonBase).ToList();
        Assert.IsTrue(interactive.Count >= 10);
        foreach (var control in interactive)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), $"{control.GetType().Name} has no automation name");
        }
    });

    [TestMethod]
    public Task LockedStartup_DisablesSwitchAndShowsNote() => UiHost.RunAsync(async () =>
    {
        var window = Open(new StartupState(false, false, "Turned off in Settings › Apps › Startup."));
        await UiHost.Settle();

        var startupSwitch = window.Controls().OfType<ToggleSwitch>().Single(t => (string)t.Header == "Start at sign-in");
        Assert.IsFalse(startupSwitch.IsEnabled);
        Assert.IsFalse(startupSwitch.IsOn);
        Assert.IsTrue(window.Model.HasStartupNote);
    });
}
```

- [ ] **Step 4: Write the HUD tests** `tests/Layers.UITests/HudWindowTests.cs`

```csharp
using Layers.Core.Logic;
using Layers.UI;
using Microsoft.UI.Windowing;
using Windows.Win32;

namespace Layers.UITests;

[TestClass]
public sealed class HudWindowTests
{
    private const uint WsExTopmost     = 0x0000_0008;
    private const uint WsExTransparent = 0x0000_0020;
    private const uint WsExToolWindow  = 0x0000_0080;
    private const uint WsExLayered     = 0x0008_0000;
    private const uint WsExNoActivate  = 0x0800_0000;

    [TestMethod]
    public Task Show_HasOverlayStyles() => UiHost.RunAsync(async () =>
    {
        var hud = new HudWindow();
        hud.Show(new LayerMask(0b100));
        await UiHost.Settle();

        var style = WindowStyles.GetExtendedStyle(hud.Handle);
        foreach (var bit in new[] { WsExTopmost, WsExTransparent, WsExToolWindow, WsExLayered, WsExNoActivate })
        {
            Assert.AreEqual(bit, style & bit, $"missing 0x{bit:X}");
        }

        hud.Close();
    });

    [TestMethod]
    public Task Show_DoesNotTakeFocus() => UiHost.RunAsync(async () =>
    {
        var hud    = new HudWindow();
        var before = PInvoke.GetForegroundWindow();

        hud.Show(new LayerMask(0b10));
        await UiHost.Settle();

        Assert.AreNotEqual((nint)hud.Handle, (nint)PInvoke.GetForegroundWindow().Value);
        Assert.AreEqual(before, PInvoke.GetForegroundWindow());
        hud.Close();
    });

    [TestMethod]
    public Task Show_SetsLabel() => UiHost.RunAsync(async () =>
    {
        var hud = new HudWindow();

        hud.Show(new LayerMask(0b1010));
        await UiHost.Settle();

        Assert.AreEqual("Layers 1, 3", hud.Text);
        hud.Close();
    });

    [TestMethod]
    public Task Show_SizesToLabelWithMinimum() => UiHost.RunAsync(async () =>
    {
        var hud   = new HudWindow();
        var scale = WindowStyles.PrimaryScale();

        hud.Show(new LayerMask(0b10));
        var shortWidth = hud.AppWindow.Size.Width;
        hud.Show(new LayerMask(0b1010_1010));
        var longWidth = hud.AppWindow.Size.Width;
        await UiHost.Settle();

        Assert.IsTrue(shortWidth >= (int)Math.Round(HudPlacement.MinWidth * scale));
        Assert.IsTrue(longWidth > shortWidth, $"long {longWidth} not wider than short {shortWidth}");
        hud.Close();
    });

    [TestMethod]
    public Task Show_SitsBottomCenterOfPrimary() => UiHost.RunAsync(async () =>
    {
        var hud = new HudWindow();

        hud.Show(new LayerMask(0b100));
        await UiHost.Settle();

        var expected = HudPlacement.Place(DisplayArea.Primary.WorkArea, hud.AppWindow.Size, WindowStyles.PrimaryScale());
        Assert.AreEqual(expected, hud.AppWindow.Position);
        hud.Close();
    });

    [TestMethod]
    public Task Show_HidesAfterFade() => UiHost.RunAsync(async () =>
    {
        var hud = new HudWindow();

        hud.Show(new LayerMask(0b100));
        await Task.Delay(1200);

        Assert.IsFalse(hud.AppWindow.IsVisible);
        hud.Close();
    });

    [TestMethod]
    public Task Show_DuringFadeRestarts() => UiHost.RunAsync(async () =>
    {
        var hud = new HudWindow();

        hud.Show(new LayerMask(0b100));
        await Task.Delay(650);
        hud.Show(new LayerMask(0b1000));
        await Task.Delay(400);

        Assert.IsTrue(hud.AppWindow.IsVisible, "restart should hold again for 550 ms");
        hud.Close();
    });
}
```

- [ ] **Step 5: Write the tray menu tests** `tests/Layers.UITests/TrayMenuTests.cs`

```csharp
using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Layers.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

[TestClass]
public sealed class TrayMenuTests
{
    private static List<string> VisibleTexts(TrayMenuHost host) => host.MenuItems
        .Where(item => item.Visibility == Visibility.Visible)
        .Select(item => item is MenuFlyoutItem text ? text.Text : "---")
        .ToList();

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected", null)]
    [DataRow(DeviceStatus.Disconnected, "Disconnected", null)]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable", "All 8 expression slots are in use")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware", "This app supports config version 18")]
    public Task Items_MatchStatus(DeviceStatus status, string label, string? detail) => UiHost.RunAsync(async () =>
    {
        var model = new TrayMenuViewModel { State = new DeviceState(status, LayerMask.Base) };
        var host  = new TrayMenuHost(model);

        host.RefreshBindings();
        await UiHost.Settle();

        var expected = new List<string> { label };
        if (detail is not null)
        {
            expected.Add(detail);
        }

        expected.AddRange(["Layer 0", "---", "Settings…", "---", "Quit"]);
        CollectionAssert.AreEqual(expected, VisibleTexts(host));
        host.Close();
    });

    [TestMethod]
    public Task Items_UpdateLive() => UiHost.RunAsync(async () =>
    {
        var model = new TrayMenuViewModel();
        var host  = new TrayMenuHost(model);
        host.RefreshBindings();

        model.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010));
        await UiHost.Settle();

        CollectionAssert.Contains(VisibleTexts(host), "Layers 1, 3");
        CollectionAssert.Contains(VisibleTexts(host), "Connected");
        host.Close();
    });
}
```

- [ ] **Step 6: Run the UI tests and fix until they pass**

Run: `dotnet test tests/Layers.UITests`
Expected: all pass.

If the host can't start:
- **Class not registered, or XAML resource not found:** confirm `WindowsAppSDKSelfContained` is true and that `resources.pri` and `Layers.UI.pri` (or the merged pri) are in `tests/Layers.UITests/bin/...`.
- **Blocked on something you can't resolve:** report `BLOCKED` with the exact error.

Don't weaken an assertion to make a test pass. A failing UI test means a real UI bug or a harness bug, and you need to find which.

- [ ] **Step 7: Run everything**

Run: `dotnet test Layers.slnx --filter "TestCategory!=Hardware"`
Expected: all projects pass.

---

### Task 16: MSI Installer

**Files:**
- Modify: `Directory.Packages.props`, `Layers.slnx`
- Create: `src/Layers.Installer/Layers.Installer.wixproj`, `src/Layers.Installer/Package.wxs`
- Test: `tests/Layers.Tests/InstallerTests.cs`

**Interfaces:**
- Consumes: the Native AOT publish output of `src/Layers`.
- Produces: `dist/Layers-<version>.msi`, a per-user install to `%LocalAppData%\Programs\Layers`.

- [ ] **Step 1: Add the WiX packages to `Directory.Packages.props`**

```xml
        <PackageVersion Include="WixToolset.Util.wixext" Version="7.0.0" />
```

Confirm 7.0.0 is still the latest stable release of `WixToolset.Sdk` and `WixToolset.Util.wixext`.

- [ ] **Step 2: Write the failing installer tests** `tests/Layers.Tests/InstallerTests.cs`

```csharp
using System.Xml.Linq;

namespace Layers.Tests;

/// Inspects the built MSI's tables. Run by build.ps1 after the MSI is built.
[TestClass]
[TestCategory("Packaging")]
public sealed class InstallerTests
{
    private static dynamic s_database = null!;
    private static string s_version = null!;

    [ClassInitialize]
    public static void OpenMsi(TestContext context)
    {
        // Find The Repo Root And Version
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir);
        s_version = XDocument.Load(Path.Combine(dir.FullName, "Directory.Build.props")).Descendants("Version").First().Value;

        // Open The MSI Read-Only
        var msi = Path.Combine(dir.FullName, "dist", $"Layers-{s_version}.msi");
        if (!File.Exists(msi))
        {
            Assert.Inconclusive($"{msi} not built; run build.ps1");
        }

        dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer")!)!;
        s_database = installer.OpenDatabase(msi, 0);
    }

    private static List<string[]> Rows(string sql, int columns)
    {
        var rows = new List<string[]>();
        dynamic view = s_database.OpenView(sql);
        view.Execute();
        for (dynamic record = view.Fetch(); record is not null; record = view.Fetch())
        {
            rows.Add(Enumerable.Range(1, columns).Select(i => (string)record.StringData(i)).ToArray());
        }

        view.Close();
        return rows;
    }

    private static string? Property(string name) =>
        Rows($"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'", 1).FirstOrDefault()?[0];

    [TestMethod]
    public void IsPerUser()
    {
        Assert.IsNull(Property("ALLUSERS"));
        Assert.AreEqual("1", Property("MSIINSTALLPERUSER"));
    }

    [TestMethod]
    public void VersionMatchesProps()
    {
        Assert.AreEqual(s_version, Property("ProductVersion"));
    }

    [TestMethod]
    public void InstallsToLocalAppDataPrograms()
    {
        var directories = Rows("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3);

        Assert.IsTrue(directories.Any(d => d[0] == "INSTALLFOLDER" && d[2].EndsWith("Layers", StringComparison.Ordinal)));
        Assert.IsTrue(directories.Any(d => d[0] == "LocalAppDataFolder"));
    }

    [TestMethod]
    public void ShipsTheExe()
    {
        Assert.IsTrue(Rows("SELECT `FileName` FROM `File`", 1).Any(f => f[0].EndsWith("Layers.exe", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void WritesQuotedRunKey()
    {
        var run = Rows("SELECT `Root`, `Key`, `Name`, `Value` FROM `Registry`", 4)
            .Single(r => r[1] == @"Software\Microsoft\Windows\CurrentVersion\Run");

        Assert.AreEqual("1", run[0], "HKCU");
        Assert.AreEqual("Layers", run[2]);
        Assert.AreEqual("\"[INSTALLFOLDER]Layers.exe\"", run[3]);
    }

    [TestMethod]
    public void AddsStartMenuShortcut()
    {
        Assert.IsTrue(Rows("SELECT `Name` FROM `Shortcut`", 1).Any(s => s[0].EndsWith("Layers", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HasMajorUpgrade()
    {
        Assert.IsTrue(Rows("SELECT `UpgradeCode` FROM `Upgrade`", 1).Count > 0);
    }

    [TestMethod]
    public void ClosesRunningAppAndLaunchesAfterInstall()
    {
        var actions = Rows("SELECT `Action` FROM `CustomAction`", 1).Select(r => r[0]).ToList();

        Assert.IsTrue(actions.Any(a => a.Contains("CloseApplications", StringComparison.Ordinal)), "util:CloseApplication missing");
        CollectionAssert.Contains(actions, "LaunchLayers");
    }
}
```

- [ ] **Step 3: Create `src/Layers.Installer/Layers.Installer.wixproj`**

```xml
<Project Sdk="WixToolset.Sdk/7.0.0">
    <!-- MSI -->
    <PropertyGroup>
        <OutputName>Layers-$(Version)</OutputName>
        <InstallerPlatform>x64</InstallerPlatform>
        <DefineConstants>ProductVersion=$(Version);PublishDir=$(PublishDir)</DefineConstants>
        <!-- Per-user installs keep files under the profile; these ICEs assume per-machine layout -->
        <SuppressIces>ICE38;ICE64;ICE91</SuppressIces>
    </PropertyGroup>

    <!-- Extensions -->
    <ItemGroup>
        <PackageReference Include="WixToolset.Util.wixext" />
    </ItemGroup>
</Project>
```

- [ ] **Step 4: Create `src/Layers.Installer/Package.wxs`**

```xml
<Wix
    xmlns="http://wixtoolset.org/schemas/v4/wxs"
    xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">

    <!-- Per-User Package -->
    <Package
        Name="Layers"
        Manufacturer="Devin Green"
        Version="$(ProductVersion)"
        UpgradeCode="7F3C2B8E-4A61-4D0B-9E57-2C8A1F6D3B90"
        Scope="perUser"
        Compressed="yes">

        <!-- Upgrades Replace Older Versions -->
        <MajorUpgrade DowngradeErrorMessage="A newer version of Layers is already installed." />
        <MediaTemplate EmbedCab="yes" />

        <!-- Add/Remove Programs Icon -->
        <Icon Id="LayersIcon" SourceFile="..\..\assets\app.ico" />
        <Property Id="ARPPRODUCTICON" Value="LayersIcon" />

        <!-- %LocalAppData%\Programs\Layers -->
        <StandardDirectory Id="LocalAppDataFolder">
            <Directory Id="ProgramsDir" Name="Programs">
                <Directory Id="INSTALLFOLDER" Name="Layers" />
            </Directory>
        </StandardDirectory>

        <!-- App Files -->
        <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">
            <Files Include="$(PublishDir)**" />
        </ComponentGroup>

        <!-- Start Menu Shortcut -->
        <StandardDirectory Id="ProgramMenuFolder">
            <Component Id="StartMenuShortcut">
                <Shortcut
                    Id="LayersShortcut"
                    Name="Layers"
                    Target="[INSTALLFOLDER]Layers.exe"
                    WorkingDirectory="INSTALLFOLDER"
                    Icon="LayersIcon" />
                <RegistryValue
                    Root="HKCU"
                    Key="Software\Layers\Installer"
                    Name="StartMenuShortcut"
                    Type="integer"
                    Value="1"
                    KeyPath="yes" />
            </Component>
        </StandardDirectory>

        <!-- Start At Sign-In (on by default; the app's Settings can turn it off) -->
        <Component Id="RunAtSignIn" Directory="INSTALLFOLDER">
            <RegistryValue
                Root="HKCU"
                Key="Software\Microsoft\Windows\CurrentVersion\Run"
                Name="Layers"
                Type="string"
                Value="&quot;[INSTALLFOLDER]Layers.exe&quot;"
                KeyPath="yes" />
        </Component>

        <!-- Close The Running App Before Files Change (sends WM_CLOSE, which the app treats as Quit) -->
        <util:CloseApplication
            Id="CloseLayers"
            Target="Layers.exe"
            CloseMessage="yes"
            RebootPrompt="no" />

        <!-- Start Layers After Install -->
        <CustomAction
            Id="LaunchLayers"
            Directory="INSTALLFOLDER"
            ExeCommand="&quot;[INSTALLFOLDER]Layers.exe&quot;"
            Execute="immediate"
            Impersonate="yes"
            Return="asyncNoWait" />
        <InstallExecuteSequence>
            <Custom
                Action="LaunchLayers"
                After="InstallFinalize"
                Condition="NOT REMOVE" />
        </InstallExecuteSequence>

        <!-- Everything -->
        <Feature Id="Main">
            <ComponentGroupRef Id="AppFiles" />
            <ComponentRef Id="StartMenuShortcut" />
            <ComponentRef Id="RunAtSignIn" />
        </Feature>
    </Package>
</Wix>
```

The `DowngradeErrorMessage` is new user-facing text. List it in your report for the owner to approve.

- [ ] **Step 5: Publish the app and build the MSI**

```bash
dotnet publish src/Layers/Layers.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish
dotnet build src/Layers.Installer/Layers.Installer.wixproj -c Release -p:PublishDir="$(pwd -W)/artifacts/publish/" -o dist
```

Add `artifacts/` to `.gitignore`.

If WiX stops asking you to accept its Open Source Maintenance Fee EULA, **stop and report `BLOCKED`**. Accepting it is the owner's decision.

- [ ] **Step 6: Run the installer tests**

Run: `dotnet test tests/Layers.Tests --filter "TestCategory=Packaging"`
Expected: all pass.

- [ ] **Step 7: Add the installer project to the solution**

`dotnet sln Layers.slnx add src/Layers.Installer/Layers.Installer.wixproj`, then confirm `dotnet build Layers.slnx` still succeeds. If building the wixproj inside the solution fails for lack of `PublishDir`, remove it from the solution instead: `build.ps1` builds it directly.

---

### Task 17: Build Script, MSIX, Version Sync, and Parity Sign-Off

**Files:**
- Create: `build.ps1`, `tests/Layers.Tests/VersionSyncTests.cs`
- Modify: `.gitignore` (confirm `dist/` and `artifacts/`)

**Interfaces:**
- Consumes: everything.
- Produces: `dist/Layers-<version>.msi` and `dist/Layers-<version>.msix`.

- [ ] **Step 1: Write the failing version test** `tests/Layers.Tests/VersionSyncTests.cs`

```csharp
using System.Xml.Linq;

namespace Layers.Tests;

[TestClass]
public sealed class VersionSyncTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Layers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    [TestMethod]
    public void MsixVersion_MatchesProps()
    {
        var version  = XDocument.Load(Path.Combine(Root(), "Directory.Build.props")).Descendants("Version").First().Value;
        var manifest = XDocument.Load(Path.Combine(Root(), "src", "Layers", "Package.appxmanifest"));
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        Assert.AreEqual($"{version}.0", manifest.Root!.Element(ns + "Identity")!.Attribute("Version")!.Value);
    }
}
```

Run: `dotnet test Layers.slnx --filter "FullyQualifiedName~VersionSyncTests"`. It should pass already (both are 2.0.0). Change the manifest to `2.0.1.0` and confirm the test fails, then change it back. That proves the test catches drift.

- [ ] **Step 2: Create `build.ps1`**

```powershell
<#
    Builds the Layers release packages.

    Outputs:  dist\Layers-<version>.msi (per-user, unpackaged, Native AOT)
              dist\Layers-<version>.msix (packaged, Native AOT)
    Used for: producing signed GitHub release assets by hand. There's no CI.
    Needs:    .NET SDK 10 (https://dotnet.microsoft.com/download)
              Windows SDK signtool (https://learn.microsoft.com/windows/win32/seccrypto/signtool)
    Signing:  LAYERS_SIGN_CERT      path to a .pfx, or a certificate thumbprint in your store
              LAYERS_SIGN_PASSWORD  .pfx password (optional)
              LAYERS_TIMESTAMP_URL  RFC 3161 timestamp server (default http://timestamp.digicert.com)
              When LAYERS_SIGN_CERT is unset, packages are built unsigned with a warning.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Paths
$root    = $PSScriptRoot
$dist    = Join-Path $root "dist"
$publish = Join-Path $root "artifacts\publish"
$msixOut = Join-Path $root "artifacts\msix"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).SelectSingleNode("//Version").InnerText

# Stop On Tool Failure
function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

# Sign One File, Or Warn When No Certificate Is Configured
function Invoke-Sign([string]$Path) {
    if (-not $env:LAYERS_SIGN_CERT) {
        Write-Warning "LAYERS_SIGN_CERT is not set; $Path is unsigned"
        return
    }

    $signtool  = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    $timestamp = if ($env:LAYERS_TIMESTAMP_URL) { $env:LAYERS_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
    $signArgs  = @("sign", "/fd", "sha256", "/tr", $timestamp, "/td", "sha256")

    if (Test-Path $env:LAYERS_SIGN_CERT) {
        $signArgs += @("/f", $env:LAYERS_SIGN_CERT)
        if ($env:LAYERS_SIGN_PASSWORD) { $signArgs += @("/p", $env:LAYERS_SIGN_PASSWORD) }
    }
    else {
        $signArgs += @("/sha1", $env:LAYERS_SIGN_CERT)
    }

    Invoke-Step "Sign $(Split-Path $Path -Leaf)" { & $signtool @signArgs $Path }
}

# Clean Outputs
Remove-Item $dist, $publish, $msixOut -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. Tests
Invoke-Step "Test" { dotnet test (Join-Path $root "Layers.slnx") -c Release --filter "TestCategory!=Hardware&TestCategory!=Packaging" }

# 2. MSI
Invoke-Step "Publish unpackaged" { dotnet publish (Join-Path $root "src\Layers\Layers.csproj") -c Release -r win-x64 -p:PublishAot=true -o $publish }
Invoke-Sign (Join-Path $publish "Layers.exe")
Invoke-Step "Build MSI" { dotnet build (Join-Path $root "src\Layers.Installer\Layers.Installer.wixproj") -c Release "-p:PublishDir=$publish\" -o $dist }
Invoke-Sign (Join-Path $dist "Layers-$version.msi")
Invoke-Step "Check MSI" { dotnet test (Join-Path $root "tests\Layers.Tests") -c Release --filter "TestCategory=Packaging" }

# 3. MSIX
Invoke-Step "Build MSIX" {
    dotnet publish (Join-Path $root "src\Layers\Layers.csproj") -c Release -r win-x64 `
        -p:WindowsPackageType=MSIX -p:PublishAot=true -p:GenerateAppxPackageOnBuild=true `
        "-p:AppxPackageDir=$msixOut\"
}
$msix = Get-ChildItem $msixOut -Recurse -Filter *.msix | Select-Object -First 1
Copy-Item $msix.FullName (Join-Path $dist "Layers-$version.msix")
Invoke-Sign (Join-Path $dist "Layers-$version.msix")

Write-Host "Done: $dist" -ForegroundColor Green
```

- [ ] **Step 3: Run the build script**

Run: `powershell -ExecutionPolicy Bypass -File build.ps1` (or `pwsh -File build.ps1`)
Expected: `dist/Layers-2.0.0.msi` and `dist/Layers-2.0.0.msix` exist, with unsigned warnings unless `LAYERS_SIGN_CERT` is set.

If `dotnet publish` can't produce the MSIX (a known gap in some SDK versions), try the same properties with `msbuild` from a Visual Studio Developer shell, and report which one worked. If neither works, report `BLOCKED` with the error.

- [ ] **Step 4: Check the published build**

Check `artifacts/publish/Layers.exe`:
1. **Launches:** run it, then find the tray window.
2. **Single instance:** a second launch exits right away.
3. **Quits cleanly:** `taskkill /IM Layers.exe` (no `/F`) closes it.
4. **Size:** report the folder size so the owner can compare it with the Rust exe's ~1.1 MB. It will be larger: the self-contained Windows App SDK ships its own runtime.

- [ ] **Step 5: Security checks**

Run each check and report its result:
1. **No network:** `findstr /s /i "HttpClient WebRequest Socket" src\*.cs` returns nothing.
2. **No secrets:** `git grep -i -E "password|BEGIN (RSA|PRIVATE)|pfx"` shows only `build.ps1`'s environment-variable handling.
3. **Never elevates:** the manifest has `asInvoker`. Check with `mt.exe -inputresource:artifacts\publish\Layers.exe;#1 -out:con`.
4. **MSIX asks for nothing extra:** it declares only `runFullTrust` and `humaninterfacedevice`. Read the `AppxManifest.xml` inside the built package.
5. **Signing, when a certificate was set:** `signtool verify /pa dist\Layers-2.0.0.msi`.

- [ ] **Step 6: Parity checklist and report for the owner**

Walk the spec's "Parity Checklist" section. Mark each line verified-by-test (name the test), verified-by-eye (from your screenshots), or needs-owner. These need the owner:
- Real device connect and layer change: run `dotnet test tests/Layers.Tests --filter "TestCategory=Hardware"` and switch layers when asked.
- Loading a config in the HID Remapper web tool while Layers runs: the display must recover within about 4 s, and "Layer N" must still update afterwards.
- Explorer restart (`taskkill /f /im explorer.exe`, then `start explorer`): the icon returns.
- Switching the taskbar between light and dark: the icon recolors.
- Moving the taskbar to a 150% monitor: the icon resizes.
- MSI install, upgrade over itself, and uninstall. Uninstall closes a running Layers, and the Run key is removed.
- MSIX install (needs a signed package, or a trusted test certificate) and its Start at sign-in switch.

- [ ] **Step 7: Flag README lines that are now wrong (don't edit them)**

The owner's rule: content is never rewritten without asking. List these for the owner, with line numbers from `README.md`:
- The "native Rust on Win32 and Direct2D, one ~1.1 MB exe" description.
- The build instructions (`cargo build --release`, `ISCC.exe installer\layers.iss`, MSVC toolchain, Inno Setup).
- The fluentui glyph credit and the link to `assets/NOTICE-fluentui.txt`, which was deleted.
- The installer description: Inno Setup is now an MSI plus an MSIX.
- Any mention of the HUD settings living in the tray menu (they're now in the Settings window).

Propose replacement text in your report. Don't apply it.
