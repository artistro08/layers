# Shortcut Sheet Flyout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tray flyout that lists every HID Remapper mapping by layer with PowerToys-style key chips, opened from a tray left-click, a menu item, or a user-set global hotkey.

**Architecture:** `Layers.Core` learns to read the mapping and macro tables over the existing config channel and turns them into a pure sheet model with the web tool's key names. `Layers.UI` gains a borderless acrylic flyout window, a key-chip control set, and a hotkey picker, all ported from `D:\magic-buds` (itself carrying PowerToys MIT code). `App` wires the three open paths and the hotkey to the flyout.

**Tech Stack:** C# / .NET 10, WinUI 3 (Windows App SDK 2.x), Native AOT, CsWin32, MSTest (`Layers.Tests`, `Layers.UITests`), WiX MSI + MSIX.

**Spec:** `docs/superpowers/specs/2026-10-26-shortcut-sheet-flyout-design.md`

## Global Constraints

- Never commit or push. The user commits when they choose. Work on a new branch `feat/shortcut-sheet` from `main`.
- Code style: 4-space indent, camelCase locals and `_camelCase` fields, Title Case block comments (`// Read The Count`), aligned `=` in related assignment runs, full XML docblocks (`<summary>`, `<remarks>`, `<param>`, `<returns>`) on every public type and member. US English everywhere.
- Native AOT: no reflection-based binding; `x:Bind` only; `FrozenDictionary`/arrays for tables; `[UnmanagedCallersOnly]` callbacks.
- Device writes: none beyond what `DeviceService` already does. Reads use `ReadAsync` with its CRC and retry rules.
- Protocol constants: `CmdGetMapping = 6`, `CmdGetMacro = 17`. `GET_CONFIG` reply: `MappingCount` is a `ushort` at payload offset 7 (packet index 8), `OurDescriptorNumber` a `byte` at payload offset 23 (packet index 24). Macros: 32 slots, 0-based on the wire, target usage `0xFFF20001` is macro index 0 named "Macro 1". Layer targets `0xFFF1000N` are "Layer N".
- Flyout: 360 DIP wide, 12 DIP margin, right-aligned to the work area, flush to the taskbar edge; open 250 ms, close 167 ms; default hotkey `Win+Shift+L`; stored as `REG_SZ` `FlyoutShortcut` under `HKCU\Software\Layers` (empty = disabled, missing = default).
- Footer copy and glyphs: status label from `StatusText.Label`; edit button `&#xE70F;` opens `https://www.jfedor.org/hid-remapper/`; settings button `&#xE713;`. Placeholders: "Plug in your HID Remapper", "No shortcuts set up", `StatusText.Detail(VersionMismatch)`. Header caption on a failed refresh: "Couldn't refresh".
- Ported PowerToys files keep their Microsoft header. `assets/NOTICE-powertoys.txt` ships in both MSI and MSIX.
- Test commands: `dotnet build Layers.slnx -c Debug` then `dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "TestCategory!=Hardware&TestCategory!=Packaging"` and `dotnet test --project tests/Layers.UITests -c Debug --no-build`. Hardware: `--filter "TestCategory=Hardware&TestCategory!=Manual"`.

## Review Focus

1. A mapping whose layer mask is 0 never fires on the device, so it must not appear in any section and must not crash the grouping. Pinned in Task 3.
2. A mapping that targets a macro slot with no steps, or a target of `0x00000000` ("Nothing"), must render as "Macro N" with "Empty" or as a "Nothing" chip, never hex or an exception. Pinned in Tasks 2 and 3.
3. A hand-edited or corrupt `FlyoutShortcut` registry string must load as "disabled", not throw at startup. Pinned in Task 6.
4. A large table (300 mappings) must still read on the device loop without starving the verify tick, and repeated flyout opens during one read must coalesce into a single read. Pinned in Task 4.
5. Opening the flyout while disconnected must show the placeholder and a working footer, and a connect while it is open must swap in the list live. Pinned in Tasks 3 and 11.

---

### Task 1: Protocol Mapping And Macro Commands

**Files:**
- Modify: `src/Layers.Core/Logic/Protocol.cs`
- Test: `tests/Layers.Tests/ProtocolTests.cs`

**Interfaces:**
- Produces, in `Layers.Core.Logic`:
  - `public const byte CmdGetMapping = 6;` `public const byte CmdGetMacro = 17;` `public const int MacroSlots = 32;` `public const int MacroItemsPerPacket = 6;` `public const uint LayerUsagePage = 0xFFF1_0000;` `public const uint MacroUsagePage = 0xFFF2_0000;`
  - `public sealed record Mapping(uint Source, uint Target, byte LayerMask, bool Sticky, bool Tap, bool Hold)` with `public int? MacroIndex` (0-based, null unless `Target` is on the macro page).
  - `public sealed record ConfigSummary(byte Version, ushort MappingCount, byte DescriptorNumber)`
  - `public sealed record MacroChunk(byte Count, uint[] Usages)` where `Usages` has `Count` entries (0 to 6).
  - `public sealed record DeviceConfig(byte DescriptorNumber, IReadOnlyList<Mapping> Mappings, IReadOnlyList<IReadOnlyList<IReadOnlyList<uint>>> Macros)`. `Macros` has 32 entries. Each macro is a list of steps; each step is a list of usages.
  - `public static byte[] GetMapping(uint index)`: payload `u32 index`.
  - `public static byte[] GetMacro(uint macro, uint offset)`: payload `u32 macro, u32 offset`.
  - `public static bool TryParseConfigSummary(ReadOnlySpan<byte> packet, out ConfigSummary summary)`
  - `public static bool TryParseMapping(ReadOnlySpan<byte> packet, out Mapping mapping)`: payload `u32 target, u32 source, i32 scaling (ignored), u8 layer_mask, u8 flags, u8 hub_ports (ignored)`; flags bit 0 Sticky, bit 1 Tap, bit 2 Hold.
  - `public static bool TryParseMacroChunk(ReadOnlySpan<byte> packet, out MacroChunk chunk)`: payload `u8 count, 6 x u32`.
  - `public static IReadOnlyList<IReadOnlyList<uint>> AssembleMacro(IEnumerable<MacroChunk> chunks)`: concatenates usages; a 0 usage ends the current step and starts the next; trailing empty step dropped; empty input gives an empty list.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod]
public void GetMapping_PayloadIsIndexU32()
{
    var packet = Protocol.GetMapping(0x0102);
    Assert.AreEqual(Protocol.CmdGetMapping, packet[2]);
    CollectionAssert.AreEqual(new byte[] { 0x02, 0x01, 0, 0 }, packet[3..7]);
}

[TestMethod]
public void GetMacro_PayloadIsMacroThenOffset()
{
    var packet = Protocol.GetMacro(3, 12);
    Assert.AreEqual(Protocol.CmdGetMacro, packet[2]);
    CollectionAssert.AreEqual(new byte[] { 3, 0, 0, 0, 12, 0, 0, 0 }, packet[3..11]);
}

[TestMethod]
public void ConfigSummary_ReadsCountAndDescriptor()
{
    // Replies.Config Gains Optional mappingCount And descriptorNumber Arguments (Task 4 Updates The Fake)
    var reply = Replies.Config(18, mappingCount: 0x0105, descriptorNumber: 3);
    Assert.IsTrue(Protocol.TryParseConfigSummary(reply, out var summary));
    Assert.AreEqual((byte)18, summary.Version);
    Assert.AreEqual((ushort)0x0105, summary.MappingCount);
    Assert.AreEqual((byte)3, summary.DescriptorNumber);
}

[TestMethod]
public void Mapping_ParsesFieldsAndFlags()
{
    var reply = Replies.Mapping(target: 0xFFF2_0003, source: 0x0007_0004, layerMask: 0b0101, flags: 0b111);
    Assert.IsTrue(Protocol.TryParseMapping(reply, out var mapping));
    Assert.AreEqual(0x0007_0004u, mapping.Source);
    Assert.AreEqual(0xFFF2_0003u, mapping.Target);
    Assert.AreEqual((byte)0b0101, mapping.LayerMask);
    Assert.IsTrue(mapping.Sticky && mapping.Tap && mapping.Hold);
    Assert.AreEqual(2, mapping.MacroIndex);
}

[TestMethod]
public void Mapping_NonMacroTargetHasNoMacroIndex()
{
    var reply = Replies.Mapping(target: 0x000C_00E9, source: 0x0007_0004, layerMask: 1, flags: 0);
    Assert.IsTrue(Protocol.TryParseMapping(reply, out var mapping));
    Assert.IsNull(mapping.MacroIndex);
}

[TestMethod]
public void Mapping_BadCrcFails()
{
    var reply = Replies.Mapping(1, 2, 1, 0);
    reply[5] ^= 0xFF;
    Assert.IsFalse(Protocol.TryParseMapping(reply, out _));
}

[TestMethod]
public void MacroChunk_ReadsCountedUsages()
{
    var reply = Replies.MacroChunk([0x000700E0, 0x00070006, 0, 0x00070019]);
    Assert.IsTrue(Protocol.TryParseMacroChunk(reply, out var chunk));
    Assert.AreEqual((byte)4, chunk.Count);
    CollectionAssert.AreEqual(new uint[] { 0x000700E0, 0x00070006, 0, 0x00070019 }, chunk.Usages);
}

[TestMethod]
public void AssembleMacro_SplitsStepsOnZero()
{
    var steps = Protocol.AssembleMacro([
        new MacroChunk(6, [0x000700E0, 0x00070006, 0, 0x000700E0, 0x00070019, 0]),
        new MacroChunk(1, [0x00070004]),
    ]);
    Assert.HasCount(3, steps);
    CollectionAssert.AreEqual(new uint[] { 0x000700E0, 0x00070006 }, steps[0].ToArray());
    CollectionAssert.AreEqual(new uint[] { 0x000700E0, 0x00070019 }, steps[1].ToArray());
    CollectionAssert.AreEqual(new uint[] { 0x00070004 }, steps[2].ToArray());
}

[TestMethod]
public void AssembleMacro_EmptyIsEmpty() => Assert.IsEmpty(Protocol.AssembleMacro([new MacroChunk(0, [])]));
```

- [ ] **Step 2: Add the reply builders to `tests/Layers.Tests/Fakes/Replies.cs`**

`Config(byte version, ushort mappingCount = 0, byte descriptorNumber = 0)` writes count at packet index 8 and descriptor at 24. New `Mapping(uint target, uint source, byte layerMask, byte flags)` writes payload `target, source, scaling 0, layerMask, flags, hubPorts 0` starting at packet index 1 (replies have no command byte; payload starts right after the report ID, matching `Replies.Expression`). New `MacroChunk(uint[] usages)` writes `count` at index 1 then up to 6 `u32`. All signed with the same CRC helper the file already uses.

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build Layers.slnx -c Debug` — Expected: compile errors on the new `Protocol` members.

- [ ] **Step 4: Implement in `Protocol.cs`**

Add the constants under CONSTANTS, the records above `Protocol` beside `SlotContents`, the two builders under COMMANDS, the three parsers under PARSING, and `AssembleMacro` under a new `// MACROS` banner. Reply payload offsets are packet index = payload offset + 1.

- [ ] **Step 5: Run tests**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~ProtocolTests"` — Expected: all pass.

---

### Task 2: Usage Name Table

**Files:**
- Create: `src/Layers.Core/Logic/UsageNames.cs` (generated, large)
- Test: `tests/Layers.Tests/UsageNamesTests.cs`

**Interfaces:**
- Produces `public static class UsageNames` in `Layers.Core.Logic`:
  - `public static string Input(uint usage)`: order `source_0`, `source`, `source_extra`, then Button page (`0x0009xxxx` → `"Button N"`), MIDI page (`0xFFF7xxxx`, rules copied from `readable_usage_name`), else `Hex(usage)`.
  - `public static string Output(uint usage, byte descriptorNumber)`: the descriptor table (`0`, `2`, `3`, `4`, `5`; `1` maps to `0`; unknown descriptor falls back to `0`), which includes the `common_target_usages` names, then `Input(usage)` rules, else `Hex`.
  - `public static string Hex(uint usage)` → `"0x0007001E"` (uppercase, 8 digits, `0x` prefix).
  - `public static bool IsMacro(uint usage)` and `public static bool IsLayer(uint usage)` by page.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod]
[DataRow(0x00070004u, "A")]
[DataRow(0x000700E0u, "Left Ctrl")]
[DataRow(0x00090001u, "Left button")]
[DataRow(0x00090010u, "Button 16")]
[DataRow(0x000C00E9u, "Volume up")]
[DataRow(0xFFF70090u, "MIDI CH0 note 144")]
[DataRow(0x12345678u, "0x12345678")]
public void Input_MatchesWebTool(uint usage, string expected) => Assert.AreEqual(expected, UsageNames.Input(usage));

[TestMethod]
[DataRow(0xFFF10002u, "Layer 2")]
[DataRow(0xFFF20001u, "Macro 1")]
[DataRow(0x00000000u, "Nothing")]
[DataRow(0x00070004u, "A")]
public void Output_UsesDescriptorTable(uint usage, string expected) => Assert.AreEqual(expected, UsageNames.Output(usage, 0));

[TestMethod]
public void Output_UnknownDescriptorFallsBackToZero() =>
    Assert.AreEqual(UsageNames.Output(0x00070004, 0), UsageNames.Output(0x00070004, 9));

[TestMethod]
public void Pages_AreRecognized()
{
    Assert.IsTrue(UsageNames.IsMacro(0xFFF20005));
    Assert.IsTrue(UsageNames.IsLayer(0xFFF10001));
    Assert.IsFalse(UsageNames.IsMacro(0x00070004));
}
```

Before writing `DataRow` strings, confirm each expected name against the downloaded `usages.js` (the "Left Ctrl" and "Volume up" spellings must be copied exactly from the file).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Layers.slnx -c Debug` — Expected: `UsageNames` not found.

- [ ] **Step 3: Generate `UsageNames.cs`**

Write a throwaway script in the scratchpad (not committed) that reads `https://raw.githubusercontent.com/jfedor2/hid-remapper/master/config-tool-web/usages.js`, parses the `usages` object and `common_target_usages`, and emits one `FrozenDictionary<uint, string>` per table (`Source0`, `Source`, `SourceExtra`, `Descriptor0`, `Descriptor2`, `Descriptor3`, `Descriptor4`, `Descriptor5`) with `common_target_usages` merged into each descriptor table, as `FrozenDictionary.ToFrozenDictionary` over a `KeyValuePair<uint,string>[]`. File header: `// Generated from jfedor2/hid-remapper config-tool-web/usages.js (MIT), commit <hash>, <date>. Do not edit by hand.` Then write `Input`, `Output`, `Hex`, `IsMacro`, `IsLayer` by hand at the bottom of the same file inside the partial class. Record the upstream commit hash with `git ls-remote https://github.com/jfedor2/hid-remapper master`.

- [ ] **Step 4: Run tests**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~UsageNamesTests"` — Expected: all pass, zero analyzer warnings (the generated file may need `#pragma warning disable` for CA1861 on the big array; prefer a `static readonly` array).

---

### Task 3: Sheet Model And View Model

**Files:**
- Create: `src/Layers.Core/Logic/ShortcutSheet.cs`
- Create: `src/Layers.Core/ViewModels/ShortcutSheetViewModel.cs`
- Test: `tests/Layers.Tests/ShortcutSheetTests.cs`, `tests/Layers.Tests/ShortcutSheetViewModelTests.cs`

**Interfaces:**
- Consumes `DeviceConfig`, `Mapping`, `UsageNames`, `LayerMask`, `DeviceState`, `StatusText`, `StatusTone`.
- Produces in `Layers.Core.Logic`:
  - `public abstract record ShortcutOutput;` `public sealed record KeysOutput(IReadOnlyList<string> Keys) : ShortcutOutput;` `public sealed record MacroOutput(int Number, IReadOnlyList<IReadOnlyList<string>> Steps) : ShortcutOutput;` (`Number` is 1-based.)
  - `public sealed record ShortcutRow(IReadOnlyList<string> Input, ShortcutOutput Output, bool Sticky, bool Tap, bool Hold)`
  - `public sealed record LayerSection(int Layer, bool IsActive, IReadOnlyList<ShortcutRow> Rows)` with `public string Title => $"Layer {Layer}"`.
  - `public static class ShortcutSheet { public static IReadOnlyList<LayerSection> Build(DeviceConfig config, LayerMask active, bool markActive = true); }`
- Produces in `Layers.Core.ViewModels`:
  - `public sealed class ShortcutSheetViewModel : ObservableObject` with `DeviceState State { get; set; }`, `DeviceConfig? Config { get; set; }`, `bool RefreshFailed { get; set; }`, and derived `IReadOnlyList<LayerSection> Sections`, `string StatusLabel`, `StatusTone Tone`, `string? EmptyMessage`, `bool HasRows`, `bool ShowList`. Every setter raises change for all derived properties.
  - `Sections` is empty when `Config` is null or `State.Status` is `Disconnected` or `VersionMismatch`. `markActive` is `State.Status == Connected`.
  - `EmptyMessage`: Disconnected → "Plug in your HID Remapper"; VersionMismatch → `StatusText.Detail(VersionMismatch)`; connected-ish with `Config` null → null and `ShowList` false (list area blank while the first read runs); zero rows → "No shortcuts set up"; otherwise null.

- [ ] **Step 1: Write the failing sheet tests**

```csharp
private static DeviceConfig Config(params Mapping[] mappings) =>
    new(0, mappings, Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList());

[TestMethod]
public void Build_GroupsByLayerBitsInOrder()
{
    var config   = Config(
        new Mapping(0x00070004, 0x000C00E9, 0b0101, false, false, false),
        new Mapping(0x00070005, 0xFFF10002, 0b0001, false, false, false));
    var sections = ShortcutSheet.Build(config, LayerMask.Base);

    CollectionAssert.AreEqual(new[] { 0, 2 }, sections.Select(s => s.Layer).ToArray());
    Assert.HasCount(2, sections[0].Rows);
    Assert.HasCount(1, sections[1].Rows);
    Assert.AreEqual("A", sections[0].Rows[0].Input[0]);
    Assert.AreEqual("Volume up", ((KeysOutput)sections[0].Rows[0].Output).Keys[0]);
    Assert.AreEqual("Layer 2", ((KeysOutput)sections[0].Rows[1].Output).Keys[0]);
}

[TestMethod]
public void Build_MarksOnlyTheActiveLayer()
{
    var sections = ShortcutSheet.Build(Config(new Mapping(1, 2, 0b0110, false, false, false)), new LayerMask(0b0100));
    CollectionAssert.AreEqual(new[] { false, true }, sections.Select(s => s.IsActive).ToArray());
}

[TestMethod]
public void Build_NoActiveMarkWhenAsked() =>
    Assert.IsFalse(ShortcutSheet.Build(Config(new Mapping(1, 2, 1, false, false, false)), LayerMask.Base, markActive: false)[0].IsActive);

[TestMethod]
public void Build_SkipsMaskZero() => Assert.IsEmpty(ShortcutSheet.Build(Config(new Mapping(1, 2, 0, false, false, false)), LayerMask.Base));

[TestMethod]
public void Build_FlagsCopied()
{
    var row = ShortcutSheet.Build(Config(new Mapping(1, 2, 1, true, false, true)), LayerMask.Base)[0].Rows[0];
    Assert.IsTrue(row.Sticky); Assert.IsFalse(row.Tap); Assert.IsTrue(row.Hold);
}

[TestMethod]
public void Build_MacroRowNamesSteps()
{
    var macros    = Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList();
    macros[2]     = [[0x000700E0u, 0x00070006u], [0x00070019u]];
    var config    = new DeviceConfig(0, [new Mapping(0x00070004, 0xFFF20003, 1, false, false, false)], macros);
    var output    = (MacroOutput)ShortcutSheet.Build(config, LayerMask.Base)[0].Rows[0].Output;

    Assert.AreEqual(3, output.Number);
    Assert.HasCount(2, output.Steps);
    CollectionAssert.AreEqual(new[] { "Left Ctrl", "C" }, output.Steps[0].ToArray());
}

[TestMethod]
public void Build_EmptyMacroHasNoSteps()
{
    var output = (MacroOutput)ShortcutSheet.Build(Config(new Mapping(1, 0xFFF20001, 1, false, false, false)), LayerMask.Base)[0].Rows[0].Output;
    Assert.IsEmpty(output.Steps);
}

[TestMethod]
public void Build_NothingTargetIsNamed() =>
    Assert.AreEqual("Nothing", ((KeysOutput)ShortcutSheet.Build(Config(new Mapping(1, 0, 1, false, false, false)), LayerMask.Base)[0].Rows[0].Output).Keys[0]);
```

Replace `"Left Ctrl"` and `"C"` with the exact `usages.js` names for `0x000700E0` and `0x00070006` if they differ.

- [ ] **Step 2: Write the failing view model tests**

```csharp
[TestMethod]
public void Disconnected_ShowsPlaceholder()
{
    var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = DeviceState.Initial };
    Assert.AreEqual("Plug in your HID Remapper", vm.EmptyMessage);
    Assert.IsEmpty(vm.Sections);
    Assert.AreEqual("Disconnected", vm.StatusLabel);
    Assert.AreEqual(StatusTone.Critical, vm.Tone);
}

[TestMethod]
public void VersionMismatch_ShowsDetailAndReadsNothing()
{
    var vm = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.VersionMismatch, LayerMask.Base) };
    Assert.AreEqual("This app supports config version 18", vm.EmptyMessage);
}

[TestMethod]
public void NoSlot_ListsWithoutActiveMark()
{
    var vm = new ShortcutSheetViewModel { Config = SomeConfig(), State = new DeviceState(DeviceStatus.NoSlot, LayerMask.Base) };
    Assert.IsTrue(vm.Sections.Count > 0 && vm.Sections.All(s => !s.IsActive));
}

[TestMethod]
public void Connected_EmptyConfigSaysNoShortcuts()
{
    var vm = new ShortcutSheetViewModel { Config = EmptyConfig(), State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
    Assert.AreEqual("No shortcuts set up", vm.EmptyMessage);
}

[TestMethod]
public void Connected_BeforeFirstReadShowsNothing()
{
    var vm = new ShortcutSheetViewModel { State = new DeviceState(DeviceStatus.Connected, LayerMask.Base) };
    Assert.IsNull(vm.EmptyMessage); Assert.IsFalse(vm.ShowList);
}

[TestMethod]
public void StateChange_RaisesSections()
{
    var vm = new ShortcutSheetViewModel { Config = SomeConfig() };
    var raised = new List<string>();
    vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
    vm.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b10));
    CollectionAssert.IsSubsetOf(new[] { "Sections", "EmptyMessage", "StatusLabel", "Tone", "ShowList" }, raised);
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build Layers.slnx -c Debug` — Expected: missing types.

- [ ] **Step 4: Implement `ShortcutSheet.Build` and `ShortcutSheetViewModel`**

`Build`: for layer 0..7, collect mappings whose mask has that bit, in device order; map each to a row via `UsageNames.Input(source)` and either `MacroOutput(index + 1, steps named with UsageNames.Output(usage, descriptor))` when `MacroIndex` is set and `< Macros.Count`, else `KeysOutput([UsageNames.Output(target, descriptor)])`. Skip layers with no rows. `IsActive = markActive && active.Active.Contains(layer)`.

- [ ] **Step 5: Run tests**

Run: `dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~ShortcutSheet"` — Expected: all pass.

---

### Task 4: Device Service Reads The Config

**Files:**
- Modify: `src/Layers.Core/Services/DeviceService.cs`
- Modify: `tests/Layers.Tests/Fakes/FakeFirmware.cs`, `tests/Layers.Tests/Fakes/Replies.cs`
- Test: `tests/Layers.Tests/DeviceServiceTests.cs`

**Interfaces:**
- Consumes Task 1.
- Produces on `DeviceService`: `public event EventHandler<DeviceConfig>? ConfigChanged;` `public void RequestConfigRefresh();` `public DeviceConfig? Config { get; }` (last good read, cleared on a new session start, kept on disconnect).
- Fake: `FakeConfigChannel` gains `public List<(uint Source, uint Target, byte LayerMask, byte Flags)> Mappings { get; } = [];`, `public uint[][] Macros { get; } = new uint[32][]` (null slot = empty; a step separator is a 0 entry), `public byte DescriptorNumber { get; set; }`. `GET_CONFIG` reply uses `Mappings.Count` and `DescriptorNumber`. `GET_MAPPING n` replies with `Replies.Mapping(...)` for that index. `GET_MACRO m, offset` replies with the 6-usage chunk at `offset` of `Macros[m]`, count being the remaining entries capped at 6 (0 for a null slot or past the end).

- [ ] **Step 1: Write the failing tests**

```csharp
private List<DeviceConfig> _configs = null!;   // Setup: _configs = []; _service.ConfigChanged += (_, c) => { lock (_configs) { _configs.Add(c); } };

[TestMethod]
public async Task Connect_ReadsMappingsAfterInstall()
{
    Config.Mappings.Add((0x00070004, 0xFFF20001, 1, 0));
    Config.Mappings.Add((0x00070005, 0x000C00E9, 2, 0b10));
    Config.Macros[0] = [0x000700E0, 0x00070006, 0, 0x00070019];
    Config.DescriptorNumber = 2;

    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);

    // Install Commands First, Then GET_CONFIG, Two GET_MAPPING, One GET_MACRO, Then The Confirming GET_CONFIG
    var tail = Config.Commands.Skip(13).ToArray();
    CollectionAssert.AreEqual(new byte[] { 3, 6, 6, 17, 3 }, tail);
    var config = _configs[0];
    Assert.AreEqual((byte)2, config.DescriptorNumber);
    Assert.HasCount(2, config.Mappings);
    Assert.IsTrue(config.Mappings[1].Tap);
    Assert.HasCount(2, config.Macros[0]);
    Assert.IsEmpty(config.Macros[1]);
    Assert.AreSame(config, _service.Config);
}

[TestMethod]
public async Task Connect_LongMacroReadsAllChunks()
{
    Config.Mappings.Add((1, 0xFFF20002, 1, 0));
    Config.Macros[1] = Enumerable.Range(1, 13).Select(i => (uint)i).ToArray();   // 13 Usages, No Separators: 6 + 6 + 1

    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);

    Assert.AreEqual(3, Config.Count(Protocol.CmdGetMacro));
    Assert.HasCount(13, _configs[0].Macros[1][0]);
}

[TestMethod]
public async Task Connect_NoSlotStillReadsMappings()
{
    Config.FillAllSlots();
    Config.Mappings.Add((1, 2, 1, 0));

    await ConnectAsync(DeviceStatus.NoSlot);
    await Eventually.Until(() => _configs.Count == 1);
}

[TestMethod]
public async Task Connect_WrongVersionReadsNoMappings()
{
    Config.Version = 19;
    Config.Mappings.Add((1, 2, 1, 0));

    await ConnectAsync(DeviceStatus.VersionMismatch);
    await Eventually.Settle();

    Assert.AreEqual(0, Config.Count(Protocol.CmdGetMapping));
    Assert.IsEmpty(_configs);
}

[TestMethod]
public async Task Refresh_RunsOnNextTickAndCoalesces()
{
    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);
    var before = Config.Count(Protocol.CmdGetConfig);

    _service.RequestConfigRefresh();
    _service.RequestConfigRefresh();
    _service.RequestConfigRefresh();
    _time.Advance(DeviceService.VerifyInterval);
    await Eventually.Until(() => _configs.Count == 2);
    await Eventually.Settle();

    Assert.AreEqual(before + 2, Config.Count(Protocol.CmdGetConfig), "one read: opening GET_CONFIG and confirming GET_CONFIG");
}

[TestMethod]
public async Task Refresh_DiscardsWhenCountMovesMidRead()
{
    Config.Mappings.Add((1, 2, 1, 0));
    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);

    // The Web Tool Saves While We Read: The Second GET_CONFIG Sees A Different Count
    Config.ForeignReply = (Protocol.CmdGetConfig, Replies.Config(18, mappingCount: 5));
    _service.RequestConfigRefresh();
    _time.Advance(DeviceService.VerifyInterval);
    await Eventually.Settle();

    Assert.HasCount(1, _configs);
}

[TestMethod]
public async Task Refresh_BadReadRaisesNothing()
{
    Config.Mappings.Add((1, 2, 1, 0));
    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);

    Config.ShortReplies = DeviceService.ReadAttempts;   // The First GET_MAPPING Exhausts Its Retries
    _service.RequestConfigRefresh();
    _time.Advance(DeviceService.VerifyInterval);
    // Walk The Doubling Retry Delays To Exhaustion
    for (var i = 0; i < DeviceService.ReadAttempts + 2; i++) { _time.Advance(TimeSpan.FromSeconds(5)); await Eventually.Settle(); }

    Assert.HasCount(1, _configs);
    Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
}

[TestMethod]
public async Task Refresh_LargeTableDoesNotBlockVerify()
{
    for (var i = 0; i < 300; i++) { Config.Mappings.Add(((uint)i, 2, 1, 0)); }
    await ConnectAsync();
    await Eventually.Until(() => _configs.Count == 1);
    Assert.HasCount(300, _configs[0].Mappings);

    var verifies = Config.Count(Protocol.CmdGetExpression);
    _time.Advance(DeviceService.VerifyInterval);
    await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) > verifies);
}
```

Note: `ForeignReply` in the fake is served to the next `GetFeature` after the named command. For `Refresh_DiscardsWhenCountMovesMidRead` the first `GET_CONFIG` of the refresh gets the foreign count 5 while the confirming one reports 1; the two differ so the read is discarded. Adjust which `GET_CONFIG` is poisoned if the fake serves it to the opening read instead; either order must discard.

Also update the existing ordering assertions (`Connect_SendsSequenceInOrder` expects exactly `[3, 21×9, 20, 11, 22]`): with zero mappings the read appends `3, 3` (opening and confirming `GET_CONFIG`), so that test's expected array gains `3, 3` and any tick-count tests that index `Config.Sent` shift by two. Re-pin them rather than loosening them.

- [ ] **Step 2: Extend the fakes per the Interfaces block**

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~DeviceServiceTests"` — Expected: new tests fail; `Connect_SendsSequenceInOrder` fails on the new tail.

- [ ] **Step 4: Implement in `DeviceService.cs`**

- Field `private volatile bool _refreshRequested;` and `private DeviceConfig? _config;` under `_gate`.
- `RequestConfigRefresh()` sets the flag. No wake-up: the verify loop checks it each tick (within 2 s).
- `private async Task<DeviceConfig?> ReadConfigAsync(IHidChannel config, CancellationToken token)`: `TryParseConfigSummary` via `ReadAsync`; loop `GetMapping(i)` with `TryParseMapping`; collect distinct `MacroIndex` values `< MacroSlots`; for each, loop `GetMacro(m, offset)` with `TryParseMacroChunk` until `Count < MacroItemsPerPacket`, then `AssembleMacro`; read `GET_CONFIG` again and return null if `MappingCount` or `DescriptorNumber` differ. Catch non-cancel exceptions, `Trace.TraceWarning`, return null.
- In `RunSessionAsync`, after `InstallAsync` returns (both `Connected` and `NoSlot`) and before `Publish`, call `await ReadAndPublishConfigAsync(...)`, which stores under `_gate` and raises `ConfigChanged` when non-null. Then inside the tick loop, before the `Status != Connected` `continue`, do: `if (_refreshRequested) { _refreshRequested = false; await ReadAndPublishConfigAsync(...); }`. Reset `_config` to null at the top of `RunSessionAsync` without raising.
- Class remarks: add a paragraph on the config read, when it runs, and the no-tag caveat with the count check.

- [ ] **Step 5: Run tests**

Run: `dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "TestCategory!=Hardware&TestCategory!=Packaging"` — Expected: all pass.

---

### Task 5: Hotkey Model

**Files:**
- Create: `src/Layers.Core/Logic/Hotkey.cs` (port of `D:\magic-buds\src\MagicBuds.Presentation\Shortcuts\Hotkey.cs`)
- Create: `src/Layers.Core/Logic/HotkeySettings.cs` (port of `D:\magic-buds\src\MagicBuds.Presentation\Shortcuts\HotkeySettings.cs`, keeps its PowerToys header)
- Test: `tests/Layers.Tests/HotkeyTests.cs` (port of `D:\magic-buds\src\MagicBuds.Presentation.Tests\ShortcutTests.cs`, Hotkey and HotkeySettings cases only)

**Interfaces:**
- Produces in `Layers.Core.Logic`: `[Flags] public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }`; `public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)` with `static bool TryCreate(HotkeyModifiers, int, out Hotkey)`, `static bool TryParse(string?, out Hotkey)`, `override string ToString()` ("Ctrl+Alt+Shift+Win+Key" order), `internal static string? KeyName(int)`; `public static Hotkey Default => new(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x4C)` (L). `public sealed record HotkeySettings` exactly as in MagicBuds (`Win, Ctrl, Alt, Shift, Code, IsAltGrRisk, FromHotkey, ToHotkey, GetKeysList, IsValid, InvalidReason, IsEmpty, IsAccessibleShortcut, ToString`).

- [ ] **Step 1: Port the tests**

Copy the Hotkey and HotkeySettings tests from `ShortcutTests.cs`, drop MagicBuds action cases, change the namespace to `Layers.Tests`, and add:

```csharp
[TestMethod]
public void Default_IsWinShiftL() => Assert.AreEqual("Shift+Win+L", Hotkey.Default.ToString());

[TestMethod]
[DataRow("Win+L")]
[DataRow("Alt+F4")]
public void Reserved_AreRefused(string text)
{
    var parsed = Hotkey.TryParse(text, out var hotkey);
    Assert.IsFalse(parsed && HotkeySettings.FromHotkey(hotkey).IsValid(), $"{text} must be rejected before registration");
}
```

(`HotkeySettings.IsValid` carries the Win+L / Alt+F4 rule in MagicBuds; keep it there.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build Layers.slnx -c Debug` — Expected: missing types.

- [ ] **Step 3: Port the two files**

Namespace `Layers.Core.Logic`. Remove the Leaf Calendar / MagicBuds header lines from `Hotkey.cs` and replace with the repo's docblock style. Keep the PowerToys header on `HotkeySettings.cs`. Keep the allowed-key list (A–Z, 0–9, F1–F24, Space, Delete). Add `Default`.

- [ ] **Step 4: Run tests**

Run: `dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~HotkeyTests"` — Expected: all pass.

---

### Task 6: Shortcut Setting Storage And View Model

**Files:**
- Modify: `src/Layers.Core/Services/SettingsStore.cs`
- Modify: `src/Layers.Core/ViewModels/SettingsViewModel.cs`
- Test: `tests/Layers.Tests/SettingsStoreTests.cs`, `tests/Layers.Tests/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes Task 5.
- Produces on `SettingsStore`: `public Hotkey? LoadShortcut()` and `public void SaveShortcut(Hotkey? hotkey)`. Value name `FlyoutShortcut`, `REG_SZ`. Missing → `Hotkey.Default`. Empty or unparsable → null. Save null writes `""`.
- Produces on `SettingsViewModel`: `public Hotkey? Shortcut { get; }`, `public bool ShortcutTaken { get; set; }`, `public string? ShortcutNote { get; set; }`, `public bool HasShortcutNote => ShortcutNote is not null`, `public void SetShortcut(Hotkey? hotkey)` (saves, raises `ShortcutChanged`), `public event EventHandler<Hotkey?>? ShortcutChanged;`. The constructor loads `Shortcut` from the store.

- [ ] **Step 1: Write the failing tests**

```csharp
// SettingsStoreTests
[TestMethod] public void Shortcut_MissingIsDefault() => Assert.AreEqual(Hotkey.Default, new SettingsStore(_key.Path).LoadShortcut());
[TestMethod] public void Shortcut_EmptyIsDisabled() { _key.Set("FlyoutShortcut", "", RegistryValueKind.String); Assert.IsNull(new SettingsStore(_key.Path).LoadShortcut()); }
[TestMethod] public void Shortcut_GarbageIsDisabled() { _key.Set("FlyoutShortcut", "Ctrl+Ctrl+Q", RegistryValueKind.String); Assert.IsNull(new SettingsStore(_key.Path).LoadShortcut()); }
[TestMethod] public void Shortcut_WrongKindIsDisabled() { _key.Set("FlyoutShortcut", 7, RegistryValueKind.DWord); Assert.IsNull(new SettingsStore(_key.Path).LoadShortcut()); }
[TestMethod] public void Shortcut_RoundTrips() { var store = new SettingsStore(_key.Path); store.SaveShortcut(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'J')); Assert.AreEqual("Ctrl+Alt+J", store.LoadShortcut()!.ToString()); Assert.AreEqual("Ctrl+Alt+J", _key.Get("FlyoutShortcut")); }
[TestMethod] public void Shortcut_SaveNullWritesEmpty() { var store = new SettingsStore(_key.Path); store.SaveShortcut(null); Assert.AreEqual("", _key.Get("FlyoutShortcut")); }

// SettingsViewModelTests
[TestMethod] public void Shortcut_LoadsFromStore() { _store.SaveShortcut(null); Assert.IsNull(NewModel().Shortcut); }
[TestMethod] public void SetShortcut_SavesAndRaises()
{
    var model = NewModel(); Hotkey? raised = default;
    model.ShortcutChanged += (_, h) => raised = h;
    var hotkey = new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Alt, 'K');
    model.SetShortcut(hotkey);
    Assert.AreEqual(hotkey, raised); Assert.AreEqual(hotkey, model.Shortcut); Assert.AreEqual(hotkey, _store.LoadShortcut());
}
```

Use the file's existing fixture names for the store and model factory.

- [ ] **Step 2: Run to verify failure** — build fails on missing members.

- [ ] **Step 3: Implement** per the Interfaces block, with the same `try/catch` filter the existing `Load`/`Save` use.

- [ ] **Step 4: Run tests**

Run: `dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~Settings"` — Expected: all pass.

---

### Task 7: Tray Routing, Hotkey Registration, Icon Rect

**Files:**
- Modify: `src/Layers.Core/Services/TrayMessageRouter.cs`
- Modify: `src/Layers.UI/TrayIcon.cs`
- Create: `src/Layers.UI/GlobalHotkey.cs` (adapted from `D:\magic-buds\src\MagicBuds.App\Interop\GlobalShortcuts.cs`)
- Modify: `src/Layers.Core/NativeMethods.txt` (add `RegisterHotKey UnregisterHotKey WM_HOTKEY Shell_NotifyIconGetRect NOTIFYICONIDENTIFIER MonitorFromRect`)
- Test: `tests/Layers.Tests/TrayMessageRouterTests.cs`

**Interfaces:**
- `TrayAction` gains `OpenFlyout` and `Hotkey`. `Route`: `WM_LBUTTONUP` → `OpenFlyout`, `WM_RBUTTONUP` → `OpenMenu`, `WM_HOTKEY` → `Hotkey`.
- `TrayIcon` gains `public event EventHandler? FlyoutRequested;`, `public event EventHandler<int>? HotkeyPressed;` (wParam id), `public RectInt32? GetIconRect()` (null when the shell call fails).
- `GlobalHotkey`: `public sealed class GlobalHotkey(nint hwnd)` with `public bool Apply(Hotkey? hotkey)` (unregister then register id `1` with `MOD_NOREPEAT`; returns false and sets `IsTaken` when registration fails), `public bool IsTaken { get; }`, `public bool IsFree(Hotkey hotkey)` (probe id `0x7FFF`), `public void Dispose()`. Logs the Win32 error code only, via `Trace.TraceWarning`.

- [ ] **Step 1: Write the failing router tests**

```csharp
[TestMethod] public void LeftClick_OpensFlyout() => Assert.AreEqual(TrayAction.OpenFlyout, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, (nint)PInvoke.WM_LBUTTONUP, 0));
[TestMethod] public void RightClick_OpensMenu() => Assert.AreEqual(TrayAction.OpenMenu, TrayMessageRouter.Route(TrayMessageRouter.TrayCallbackMessage, (nint)PInvoke.WM_RBUTTONUP, 0));
[TestMethod] public void Hotkey_Routes() => Assert.AreEqual(TrayAction.Hotkey, TrayMessageRouter.Route(PInvoke.WM_HOTKEY, 0, 0));
```

Update the existing test that expects both buttons to open the menu.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

Router and `TrayIcon.WindowProc` cases (`Hotkey` raises `HotkeyPressed(wParam)`). `GetIconRect` uses `Shell_NotifyIconGetRect` with `NOTIFYICONIDENTIFIER { cbSize, hWnd = _hwnd, uID = IconId }`. `GlobalHotkey` is the MagicBuds class reduced to one registration and no `ILogger`.

- [ ] **Step 4: Run tests**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.Tests -c Debug --no-build --filter "FullyQualifiedName~TrayMessageRouter"` — Expected: pass. Also run the full non-hardware suite to confirm nothing else broke.

---

### Task 8: Flyout Placement Math

**Files:**
- Create: `src/Layers.Core/Logic/FlyoutPlacement.cs` (port of `D:\magic-buds\src\MagicBuds.Presentation\Placement\FlyoutPlacement.cs`)
- Test: `tests/Layers.Tests/FlyoutPlacementTests.cs`

**Interfaces:**
- Produces in `Layers.Core.Logic`: `public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)` with `Width`, `Height`; `public enum TaskbarEdge { Bottom, Top, Left, Right }`; `public static class FlyoutPlacement { public const int WidthDip = 360; public const int MarginDip = 12; public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea); public static PixelRect Calculate(PixelRect monitor, PixelRect workArea, double scale, double heightDip); }`. `HeightDip` constant is dropped; the caller passes the work area height in DIP (the window is as tall as the work area allows; the panel sizes itself inside).

- [ ] **Step 1: Write the failing tests**

```csharp
private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);

[TestMethod]
[DataRow(0, 0, 2560, 1392, TaskbarEdge.Bottom)]
[DataRow(0, 48, 2560, 1440, TaskbarEdge.Top)]
[DataRow(64, 0, 2560, 1440, TaskbarEdge.Left)]
[DataRow(0, 0, 2496, 1440, TaskbarEdge.Right)]
[DataRow(0, 0, 2560, 1440, TaskbarEdge.Bottom)]   // Auto-Hide
public void DetectEdge(int l, int t, int r, int b, TaskbarEdge expected) => Assert.AreEqual(expected, FlyoutPlacement.DetectEdge(Monitor, new PixelRect(l, t, r, b)));

[TestMethod]
public void Calculate_BottomTaskbarAt150Percent()
{
    var work = new PixelRect(0, 0, 2560, 1392);
    var rect = FlyoutPlacement.Calculate(Monitor, work, 1.5, 500);
    Assert.AreEqual(540, rect.Width);            // 360 * 1.5
    Assert.AreEqual(750, rect.Height);           // 500 * 1.5
    Assert.AreEqual(2560 - 18, rect.Right);      // 12 * 1.5 Margin
    Assert.AreEqual(1392 - 18, rect.Bottom);
}

[TestMethod]
public void Calculate_TopTaskbarHangsFromTop()
{
    var rect = FlyoutPlacement.Calculate(Monitor, new PixelRect(0, 48, 2560, 1440), 1.0, 500);
    Assert.AreEqual(60, rect.Top);
}

[TestMethod]
public void Calculate_ClampsToShortWorkArea()
{
    var rect = FlyoutPlacement.Calculate(Monitor, new PixelRect(0, 0, 2560, 300), 1.0, 10_000);
    Assert.AreEqual(300 - 24, rect.Height);
}
```

- [ ] **Step 2: Run to verify failure.**  - [ ] **Step 3: Port the file** with repo docblocks.  - [ ] **Step 4: Run tests** — filter `FlyoutPlacementTests`, expected pass.

---

### Task 9: Key Chip Controls

**Files:**
- Create: `src/Layers.UI/Controls/KeyVisual.cs`, `src/Layers.UI/Controls/KeyCharPresenter.cs`, `src/Layers.UI/Styles/KeyVisual.xaml`, `src/Layers.UI/Styles/KeyCharPresenter.xaml` (ports from `D:\magic-buds\src\MagicBuds.App\Controls\*` and `Styles\*`, PowerToys headers kept)
- Modify: `src/Layers/App.xaml` and `tests/Layers.UITests/TestApp.xaml` (merge the two dictionaries, `KeyCharPresenter.xaml` first, using `ms-appx:///Layers.UI/Styles/...` URIs)
- Test: `tests/Layers.UITests/KeyVisualTests.cs`

**Interfaces:**
- Produces `Layers.UI.Controls.KeyVisual : Control` with `object Content`, `KeyVisualState State`, `bool RenderKeyAsGlyph`; styles `DefaultKeyVisualStyle`, `KeyChipStyle` as resource keys.

- [ ] **Step 1: Write the failing UI tests**

```csharp
[TestMethod]
public Task KeyVisual_RendersText() => UiHost.RunAsync(async () =>
{
    var visual = new KeyVisual { Content = "Volume up", Style = (Style)Application.Current.Resources["KeyChipStyle"] };
    var host   = UiHost.ShowInWindow(visual);     // Add A Small Helper To UiHost That Opens A Window Around A Control And Returns It
    await UiHost.Settle();
    Assert.IsNotNull(visual.FindDescendant<TextBlock>(t => t.Text == "Volume up"));   // Use The Existing Tree-Walk Helper In SettingsWindowTests Or Add One To UiHost
    host.Close();
});

[TestMethod]
public Task KeyVisual_WindowsKeyIsGlyph() => UiHost.RunAsync(async () =>
{
    var visual = new KeyVisual { Content = 92 };
    var host   = UiHost.ShowInWindow(visual);
    await UiHost.Settle();
    Assert.IsNotNull(visual.FindDescendant<Viewbox>());
    host.Close();
});
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Port the four files**

Namespace `Layers.UI.Controls`; XAML `x:Class`-less dictionaries. Replace MagicBuds-only resource names with the stock theme brushes they alias. Register the dictionaries in both `App.xaml` files.

- [ ] **Step 4: Run tests**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.UITests -c Debug --no-build --filter "FullyQualifiedName~KeyVisual"` — Expected: pass.

---

### Task 10: Sheet View

**Files:**
- Create: `src/Layers.UI/ShortcutSheetView.xaml`, `src/Layers.UI/ShortcutSheetView.xaml.cs`
- Test: `tests/Layers.UITests/ShortcutSheetViewTests.cs`

**Interfaces:**
- Consumes `ShortcutSheetViewModel` (Task 3), `KeyVisual` (Task 9), `TrayMenuHost.ToneBrush`.
- Produces `public sealed partial class ShortcutSheetView : UserControl` with ctor `(ShortcutSheetViewModel model)`, `public ShortcutSheetViewModel Model { get; }`, events `CloseRequested`, `SettingsRequested`, `EditRequested`, `internal void RefreshBindings()`, `internal void FocusFirstChip()`, and internal test hooks `internal IReadOnlyList<FrameworkElement> SectionElements`, `internal Button EditButton`, `internal Button SettingsButton`, `internal TextBlock StatusLabel`, `internal TextBlock Placeholder`, `internal TextBlock RefreshNote`.
- Layout per spec Section 3: title row, `ScrollViewer` → `ItemsRepeater` of sections (header `TextBlock` + "Active" pill `Border` + rows), placeholder `TextBlock`, footer `Grid`. Rows are built in code (`BuildSection(LayerSection)`) rather than `DataTemplate`s, so AOT needs no template binding; the `ItemsRepeater` is replaced by a `StackPanel` the code fills on `Model.PropertyChanged("Sections")`.
- Chip row: input `KeyVisual`s → `FontIcon &#xE72A;` → output chips in a wrapping panel (port `D:\magic-buds\src\MagicBuds.App\WrapPanel.cs` as `Layers.UI.Controls.WrapPanel`). Macro: caption `TextBlock "Macro N"` (`CaptionTextBlockStyle`, secondary brush) above the steps; steps joined by `FontIcon &#xE76C;` (chevron); empty steps show a "Empty" tag. Flags as `Border` tags with caption text.

- [ ] **Step 1: Write the failing UI tests**

```csharp
private static ShortcutSheetViewModel Connected(DeviceConfig config) => new() { Config = config, State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b100)) };

[TestMethod]
public Task Sections_RenderPerLayerWithActivePill() => UiHost.RunAsync(async () =>
{
    var view = new ShortcutSheetView(Connected(TwoLayerConfig()));   // Layers 0 And 2, Active 2
    var host = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    Assert.HasCount(2, view.SectionElements);
    Assert.AreEqual(1, view.FindDescendants<Border>(b => b.Tag is "Active").Count());
    host.Close();
});

[TestMethod]
public Task MacroRow_ShowsCaptionAndEveryStep() => UiHost.RunAsync(async () =>
{
    var view = new ShortcutSheetView(Connected(MacroConfig()));       // One Mapping To Macro 3 With Steps [Ctrl,C] [V]
    var host = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Macro 3"));
    Assert.AreEqual(4, view.FindDescendants<KeyVisual>().Count());      // A, Ctrl, C, V
    Assert.AreEqual(1, view.FindDescendants<FontIcon>(f => f.Glyph == "\uE76C").Count());
    host.Close();
});

[TestMethod]
public Task Disconnected_ShowsPlaceholderAndFooterWorks() => UiHost.RunAsync(async () =>
{
    var view = new ShortcutSheetView(new ShortcutSheetViewModel { State = DeviceState.Initial });
    var host = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    Assert.AreEqual("Plug in your HID Remapper", view.Placeholder.Text);
    Assert.AreEqual("Disconnected", view.StatusLabel.Text);
    var edit = false; var settings = false;
    view.EditRequested += (_, _) => edit = true; view.SettingsRequested += (_, _) => settings = true;
    UiHost.Invoke(view.EditButton); UiHost.Invoke(view.SettingsButton);
    Assert.IsTrue(edit && settings);
    Assert.AreEqual("Edit in browser", AutomationProperties.GetName(view.EditButton));
    Assert.AreEqual("Settings", AutomationProperties.GetName(view.SettingsButton));
    host.Close();
});

[TestMethod]
public Task Connect_WhileOpenSwapsInList() => UiHost.RunAsync(async () =>
{
    var model = new ShortcutSheetViewModel { State = DeviceState.Initial };
    var view  = new ShortcutSheetView(model);
    var host  = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    model.Config = TwoLayerConfig();
    model.State  = new DeviceState(DeviceStatus.Connected, LayerMask.Base);
    await UiHost.Settle();
    Assert.AreEqual(Visibility.Collapsed, view.Placeholder.Visibility);
    Assert.HasCount(2, view.SectionElements);
    host.Close();
});

[TestMethod]
public Task RefreshFailed_ShowsNote() => UiHost.RunAsync(async () =>
{
    var model = Connected(TwoLayerConfig()); model.RefreshFailed = true;
    var view  = new ShortcutSheetView(model);
    var host  = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    Assert.AreEqual("Couldn't refresh", view.RefreshNote.Text);
    Assert.AreEqual(Visibility.Visible, view.RefreshNote.Visibility);
    host.Close();
});

[TestMethod]
public Task Escape_RaisesClose() => UiHost.RunAsync(async () =>
{
    var view = new ShortcutSheetView(Connected(TwoLayerConfig()));
    var closed = false; view.CloseRequested += (_, _) => closed = true;
    var host = UiHost.ShowInWindow(view);
    await UiHost.Settle();
    Assert.IsTrue(view.KeyboardAccelerators.Any(a => a.Key == VirtualKey.Escape), "no Escape accelerator");
    view.RequestClose();                                 // Internal Method The Accelerator's Invoked Handler Calls
    Assert.IsTrue(closed);
    host.Close();
});
```

`UiHost.Invoke(Button)` uses `ButtonAutomationPeer.Invoke()` as `SettingsWindowTests` already does; lift that into `UiHost` if it is local there.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement the view** per the Interfaces block. Sizes: chips `KeyChipStyle`, `MinWidth 36`, `Padding 8`, `FontSize 14`; section header `BodyStrongTextBlockStyle`; row spacing 8; section spacing 16; footer 48 DIP tall with `LayerFillColorDefaultBrush` background and a top `DividerStrokeColorDefaultBrush` line; footer buttons 36x36 with the stock `SubtleButtonStyle`-equivalent (`ButtonRevealStyle` is not available; use a `Style` based on `DefaultButtonStyle` with transparent background and border, named `FooterIconButtonStyle` in the view's resources).

- [ ] **Step 4: Run tests**

Run: `dotnet build Layers.slnx -c Debug; dotnet test --project tests/Layers.UITests -c Debug --no-build --filter "FullyQualifiedName~ShortcutSheetView"` — Expected: pass.

---

### Task 11: Flyout Window

**Files:**
- Create: `src/Layers.UI/ShortcutFlyoutWindow.xaml`, `src/Layers.UI/ShortcutFlyoutWindow.xaml.cs` (port of `D:\magic-buds\src\MagicBuds.App\FlyoutWindow.*`)
- Create: `src/Layers.UI/PopupWindowStyle.cs`, `src/Layers.UI/TransparentBackdrop.cs`, `src/Layers.UI/ScreenGeometry.cs` (ports; `ScreenGeometry` returns `(PixelRect Monitor, PixelRect WorkArea, double Scale)` for a `RectInt32?` icon rect)
- Create: `src/Layers.Core/Services/Windhawk.cs` (port of `D:\magic-buds\src\MagicBuds.App\Windhawk.cs`)
- Modify: `src/Layers.Core/NativeMethods.txt` (add `SetWindowRgn CreateRectRgn SetWindowSubclass DefSubclassProc RemoveWindowSubclass DwmSetWindowAttribute DwmExtendFrameIntoClientArea DwmEnableBlurBehindWindow DWM_BLURBEHIND DWMWINDOWATTRIBUTE DWM_WINDOW_CORNER_PREFERENCE WM_STYLECHANGING WM_ERASEBKGND STYLESTRUCT GWL_STYLE` and whatever the ported files reference that CsWin32 can supply; hand-written `[LibraryImport]` only for what it cannot)
- Test: `tests/Layers.UITests/ShortcutFlyoutWindowTests.cs`, `tests/Layers.Tests/WindhawkTests.cs`

**Interfaces:**
- Consumes `ShortcutSheetView` (Task 10), `FlyoutPlacement` (Task 8), existing `ActiveAcrylicBackdrop`.
- Produces `public sealed partial class ShortcutFlyoutWindow : Window`: ctor `(ShortcutSheetViewModel model)`; `public void Open(RectInt32? iconRect)`; `public void Toggle(RectInt32? iconRect)`; `public void HideFlyout()`; `public void Shutdown()`; `public bool IsOpen { get; }`; events `Opened`, `CloseRequested`, `SettingsRequested`, `EditRequested` (the last three re-raised from the view). `Windhawk.HasTrayOnEveryTaskbar()` static, reads `HKLM\SOFTWARE\Windhawk\Engine\Mods\*taskbar-multi-tray*`, false on any error.
- Panel height: content height clamped to the window height; the view's `ScrollViewer` takes the rest.

- [ ] **Step 1: Write the failing tests**

```csharp
// WindhawkTests (Layers.Tests)
[TestMethod] public void NoWindhawk_IsFalse() => Assert.IsFalse(Windhawk.HasTrayOnEveryTaskbar(@"Software\LayersTests\NoSuchKey"));   // Give The Method An Optional keyPath Parameter For Tests

// ShortcutFlyoutWindowTests (Layers.UITests)
[TestMethod]
public Task Open_ThenHide_TogglesIsOpenAndRaisesOpened() => UiHost.RunAsync(async () =>
{
    var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
    var opened = 0; window.Opened += (_, _) => opened++;
    window.Open(null);
    await UiHost.Settle();
    Assert.IsTrue(window.IsOpen); Assert.AreEqual(1, opened);
    window.HideFlyout();
    await UiHost.Settle(TimeSpan.FromMilliseconds(400));
    Assert.IsFalse(window.IsOpen);
    window.Shutdown();
});

[TestMethod]
public Task Toggle_IgnoresBounceWithin300ms() => UiHost.RunAsync(async () =>
{
    var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
    window.Toggle(null); await UiHost.Settle();
    window.Toggle(null); window.Toggle(null);
    await UiHost.Settle(TimeSpan.FromMilliseconds(400));
    Assert.IsFalse(window.IsOpen);
    window.Shutdown();
});

[TestMethod]
public Task Window_IsBorderlessPopupNotInSwitcher() => UiHost.RunAsync(async () =>
{
    var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
    window.Open(null); await UiHost.Settle();
    Assert.IsFalse(window.AppWindow.IsShownInSwitchers);
    var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
    Assert.IsTrue(presenter.IsAlwaysOnTop); Assert.IsFalse(presenter.HasBorder); Assert.IsFalse(presenter.HasTitleBar);
    window.Shutdown();
});
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Port the window and helpers**

Strip `FlyoutViewModel`, `AppTheme`, `ImplicitMotion`, `OnOpened`, and `"MagicBuds"` strings. Use `Layers.UI.ActiveAcrylicBackdrop`. `Open` calls `Model`-free `PlaceWindow(iconRect)` then raises `Opened` after the slide starts. Hotkey-opened calls pass `iconRect` null when `Windhawk.HasTrayOnEveryTaskbar()`; `App` decides that (Task 13). Convert `NativeMethods.RECT` to `RectInt32`. Where the MagicBuds code hand-wrote `DllImport`s, prefer CsWin32 entries; keep `[LibraryImport]` only where CsWin32 lacks the API (`SetWindowCompositionAttribute`).

- [ ] **Step 4: Run tests**

Run: both test projects, expected pass. Then a manual check: `dotnet run --project src/Layers -c Debug` is not how this app starts; instead build and launch `src\Layers\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Layers.exe` after Task 13 wires it.

---

### Task 12: Hotkey Picker And Settings Page

**Files:**
- Create: `src/Layers.UI/Interop/KeyboardHook.cs` (port, PowerToys header), `src/Layers.UI/Controls/ShortcutControl.xaml(.cs)`, `src/Layers.UI/Controls/ShortcutDialogContentControl.xaml(.cs)` (ports, PowerToys headers), `src/Layers.UI/ShortcutsPage.xaml(.cs)`
- Modify: `src/Layers.UI/SettingsWindow.xaml.cs` (nav item between General and About, glyph `0xE765`, navigation case), `src/Layers.Core/NativeMethods.txt` (`SetWindowsHookEx UnhookWindowsHookEx CallNextHookEx KBDLLHOOKSTRUCT HC_ACTION GetAsyncKeyState SendInput INPUT KEYBDINPUT KEYEVENTF_KEYUP WH_KEYBOARD_LL`)
- Test: `tests/Layers.UITests/SettingsWindowTests.cs` (new cases), `tests/Layers.UITests/ShortcutControlTests.cs`

**Interfaces:**
- Consumes Task 5, Task 6, Task 9. `ShortcutControl` API as in MagicBuds: `HotkeySettings? HotkeySettings`, `HotkeySettings DefaultHotkeySettings`, `Func<Hotkey, string?>? CheckConflict`, `bool HasConflict`, `string ButtonName`, events `HotkeySettingsChanged`, `DialogOpening`, `DialogClosed`.
- `ShortcutsPage : Page` navigated with `SettingsViewModel` as parameter, exposing `internal ShortcutControl Picker` and `internal InfoBar Note`. `CheckConflict` is supplied by `SettingsWindow` through a new `public Func<Hotkey, bool>? IsHotkeyFree { get; set; }` on `SettingsViewModel` that `App` sets to `GlobalHotkey.IsFree`. Copy: card title "Open the shortcut sheet", description "Show every shortcut on the connected HID Remapper.", conflict message "Another app or Windows already uses this shortcut.", restore note "That shortcut is taken, so the previous one was kept."

- [ ] **Step 1: Write the failing tests**

```csharp
// SettingsWindowTests
[TestMethod]
public Task ShortcutsPage_ShowsPickerWithSavedShortcut() => UiHost.RunAsync(async () =>
{
    _store.SaveShortcut(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'J'));
    var window = Open();
    window.SelectNav("Shortcuts");                    // Helper Mirroring How Existing Tests Pick "About"
    await UiHost.Settle();
    var page = (ShortcutsPage)window.ContentFrameContent;
    Assert.AreEqual("Ctrl+Alt+J", page.Picker.HotkeySettings!.ToHotkey()!.ToString());
});

[TestMethod]
public Task ShortcutsPage_TakenShortcutShowsWarning() => UiHost.RunAsync(async () =>
{
    var window = Open(model => model.ShortcutTaken = true);
    window.SelectNav("Shortcuts");
    await UiHost.Settle();
    var page = (ShortcutsPage)window.ContentFrameContent;
    Assert.IsTrue(page.Picker.HasConflict);
    Assert.IsTrue(page.Note.IsOpen);
});

// ShortcutControlTests
[TestMethod]
public Task Control_ShowsChipsForSetting() => UiHost.RunAsync(async () =>
{
    var control = new ShortcutControl { HotkeySettings = HotkeySettings.FromHotkey(new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Shift, 'L')) };
    var host = UiHost.ShowInWindow(control);
    await UiHost.Settle();
    Assert.AreEqual(3, control.FindDescendants<KeyVisual>().Count());
    host.Close();
});

[TestMethod]
public Task Control_EmptyShowsAssignPlaceholder() => UiHost.RunAsync(async () =>
{
    var control = new ShortcutControl { HotkeySettings = new HotkeySettings() };
    var host = UiHost.ShowInWindow(control);
    await UiHost.Settle();
    Assert.IsNotNull(control.FindDescendant<TextBlock>(t => t.Text == "Assign shortcut"));
    host.Close();
});
```

The dialog's key capture needs a real foreground window and a hook; it is not unit tested. It is covered by the manual check in Task 14.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Port and wire**

Replace `"MagicBuds"` in the hook-failure message with `"Layers"`. Keep Caps Lock passthrough and injected-Esc drop. `ShortcutsPage` code-behind: on load, `Picker.HotkeySettings = Model.Shortcut is { } h ? HotkeySettings.FromHotkey(h) : new HotkeySettings()`, `Picker.DefaultHotkeySettings = HotkeySettings.FromHotkey(Hotkey.Default)`, `Picker.CheckConflict = hotkey => Model.IsHotkeyFree?.Invoke(hotkey) == false ? ConflictMessage : null`; on `HotkeySettingsChanged` call `Model.SetShortcut(settings.ToHotkey())`; on `DialogOpening`/`DialogClosed` raise nothing (the `App` suspends the registration by applying null during the dialog via `Model.ShortcutDialogOpen` events: add `public event EventHandler<bool>? ShortcutDialogOpenChanged;` on the view model and raise it from the page). After `SetShortcut`, if `Model.ShortcutTaken` becomes true, restore the previous hotkey and open `Note`.

- [ ] **Step 4: Run tests** — both projects, expected pass.

---

### Task 13: Tray Menu Item And App Wiring

**Files:**
- Modify: `src/Layers.UI/TrayMenuHost.xaml`, `src/Layers.UI/TrayMenuHost.xaml.cs` ("Shortcuts" item above "Settings…", glyph `&#xE765;`, event `ShortcutsRequested`)
- Modify: `src/Layers/App.xaml.cs`
- Test: `tests/Layers.UITests/TrayMenuTests.cs`

**Interfaces:**
- `App` owns `ShortcutSheetViewModel _sheetModel`, `ShortcutFlyoutWindow? _flyout`, `GlobalHotkey? _hotkey`, `Hotkey? _shortcut`.
- Wiring: `_tray.FlyoutRequested` → `_flyout.Toggle(_tray.GetIconRect())`; `_menu.ShortcutsRequested` → close menu, then same; `_tray.HotkeyPressed` (id 1) → `_flyout.Toggle(Windhawk.HasTrayOnEveryTaskbar() ? null : _tray.GetIconRect())`; `_flyout.Opened` → `_device.RequestConfigRefresh()` and start a 5 s `DispatcherQueueTimer` that sets `_sheetModel.RefreshFailed = true` unless `ConfigChanged` arrives first; `_device.ConfigChanged` → `_sheetModel.Config = config; _sheetModel.RefreshFailed = false;` on the dispatcher; `OnDeviceState` also sets `_sheetModel.State`; `_flyout.SettingsRequested` → `HideFlyout` then `OpenSettings()`; `_flyout.EditRequested` → `HideFlyout` then `Launcher.LaunchUriAsync(new Uri("https://www.jfedor.org/hid-remapper/"))`; `_flyout.CloseRequested` → `HideFlyout`. Hotkey: `_hotkey = new GlobalHotkey(_tray.Handle)`; `_shortcut = _store.LoadShortcut()`; `_hotkey.Apply(_shortcut)`; `SettingsViewModel.IsHotkeyFree = _hotkey.IsFree`; `ShortcutChanged` → `Apply` and set `model.ShortcutTaken = _hotkey.IsTaken`; `ShortcutDialogOpenChanged(true)` → `Apply(null)`, `(false)` → `Apply(_shortcut)`. `QuitAsync` calls `_flyout?.Shutdown()` and `_hotkey?.Dispose()`.

- [ ] **Step 1: Update the failing menu test**

In `Items_MatchStatus`, the expected tail becomes `["Layer 0", "---", "Shortcuts", "Settings…", "---", "Quit"]`. Add:

```csharp
[TestMethod]
public Task Shortcuts_RaisesEvent() => UiHost.RunAsync(async () =>
{
    var host = new TrayMenuHost(new TrayMenuViewModel());
    var raised = false; host.ShortcutsRequested += (_, _) => raised = true;
    UiHost.Invoke(host.MenuItems.OfType<MenuFlyoutItem>().Single(i => i.Text == "Shortcuts"));
    await UiHost.Settle();
    Assert.IsTrue(raised);
    host.Close();
});
```

- [ ] **Step 2: Run to verify failure.**  - [ ] **Step 3: Implement the menu item and the `App` wiring** per the Interfaces block.  - [ ] **Step 4: Run tests** — both projects, expected pass.

- [ ] **Step 5: Smoke run**

Build Debug, launch `src\Layers\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Layers.exe`, and check: left-click opens the flyout above the tray; right-click opens the menu with "Shortcuts"; Win+Shift+L toggles it from another app; Esc closes; clicking elsewhere closes; the footer gear opens Settings; the pencil opens the browser; with the device plugged in the layers list fills and the active pill follows a layer switch. Close with Quit. Record the outcome in the final report.

---

### Task 14: Notice, Installer, README, Hardware Test

**Files:**
- Create: `assets/NOTICE-powertoys.txt`
- Modify: `src/Layers.Installer/Package.wxs`, `src/Layers/Layers.csproj`, `tests/Layers.Tests/InstallerTests.cs`, `README.md`, `tests/Layers.Tests/HardwareTests.cs`

- [ ] **Step 1: Write the failing installer and hardware tests**

```csharp
// InstallerTests: extend ShipsTheNoticeOnce
Assert.AreEqual(1, Rows("SELECT `FileName` FROM `File`", 1).Count(f => f[0].EndsWith("NOTICE-powertoys.txt", StringComparison.OrdinalIgnoreCase)));

// HardwareTests
[TestMethod]
public async Task RealDevice_ReadsMappingsWithNames()
{
    await RequireDeviceAsync();
    await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
    DeviceConfig? config = null;
    service.ConfigChanged += (_, c) => config = c;
    var state = await ConnectAsync(service);
    Assert.AreEqual(DeviceStatus.Connected, state.Status);
    await Eventually.Until(() => config is not null, TimeSpan.FromSeconds(10));
    var sheet = ShortcutSheet.Build(config!, LayerMask.Base);
    Assert.IsTrue(sheet.SelectMany(s => s.Rows).All(r => !r.Input[0].StartsWith("0x", StringComparison.Ordinal)), "an input had no name");
}
```

- [ ] **Step 2: Notice and packaging**

`assets/NOTICE-powertoys.txt`: PowerToys MIT text with the list of ported files (KeyVisual, KeyCharPresenter, their XAML, ShortcutControl, ShortcutDialogContentControl, KeyboardHook, HotkeySettings). `Package.wxs`: second `File` in the `NoticeFile` component (`Id="NoticePowerToys"`). `Layers.csproj`: second `Content` include in the MSIX group. Also add a line crediting jfedor2/hid-remapper's `usages.js` (MIT) to the same notice file since the generated table derives from it.

- [ ] **Step 3: README**

Credits: add PowerToys and the HID Remapper web tool's name table. New section after Settings, heading "Shortcut sheet": left-click the tray icon or press Win+Shift+L to see every shortcut on your HID Remapper grouped by layer, with the active layer marked; macros show their full key sequence; the pencil opens the web config tool to edit them; change or clear the hotkey under Settings › Shortcuts. Second person, plainspoken, matching the existing voice.

- [ ] **Step 4: Verify**

Run: `pwsh -File build.ps1` — Expected: unit, UI, and packaging tests pass, MSI and MSIX built, no new warnings. If the device is plugged in, also run the hardware filter and report the result.
