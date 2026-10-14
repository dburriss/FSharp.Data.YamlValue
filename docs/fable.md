# Fable support

The library compiles with [Fable](https://fable.io) to JavaScript and Python from the same source
(no separate files). Differences are `#if FABLE_COMPILER` branches, almost all in
`src/FSharp.Data.YamlValue/YamlPortable.fs`; on .NET every one of them is the BCL call the library
always made, so the .NET public surface and behaviour are unchanged.

## What is compiled out under Fable

The loaders that touch files, streams or the network, and `WriteTo(TextWriter, ...)`:
`YamlValue.Load` (stream, reader, uri), `YamlValue.AsyncLoad`, `YamlDocument.Load` /
`AsyncLoad`, and both `WriteTo` members. Parse from a string instead.

## Running the test suite under Fable

```bash
scripts/fable-test.sh          # JS and Python
scripts/fable-test.sh js
scripts/fable-test.sh python
```

Prerequisites:

- .NET SDK 8+ (the Fable tool is pinned to 5.21.0 in `.config/dotnet-tools.json` and restored by the script)
- JS: `bun` or `node` (any runtime that runs ES modules)
- Python: 3.12+ (3.13 tested). The script creates `tests/FSharp.Data.YamlValue.Fable/.venv` and installs `fable-library` at the Fable tool's version (network needed the first time)

xUnit and FsCheck cannot run under Fable, so `tests/FSharp.Data.YamlValue.Fable` compiles the same
test files as the .NET suite through a small `Xunit` shim (`XunitShim.fs`).
`scripts/fable-gen-runner.fsx` (run by the script) scans those files and generates the runner and
an embedded copy of `tests/.../data`. The script exits non-zero when any test fails. Current result:
all 491 tests pass on JS and on Python.

Left out of the Fable run: `RoundTripTests.fs` (FsCheck property tests) and the file/stream
loader tests (`#if !FABLE_COMPILER` in `DocumentTests.fs`).

## Known differences and limitations under Fable

- **Loaders and `WriteTo(TextWriter)`** are not available (above).
- **Timestamps:** only the ISO shapes the scalar resolver understands are recognised. `AsDateTime` /
  `AsDateTimeOffset` on a string and `!!timestamp` on a non-ISO value are not supported
  (Fable's `DateTime.TryParse` ignores culture and styles). The emitted timestamp text may spell a
  UTC offset as `Z` rather than `+00:00`, and sub-millisecond ticks are not guaranteed.
- **Python strings are code points, not UTF-16 units.** An astral character (e.g. U+1F389) has
  `Length` 1, so `Column`/`Offset` in `ParseWithPositions` count it as one unit, not two.
  A `🎉` escape still decodes to the single character.
- **Number formatting:** Python prints `Float` with its shortest round-trip text (`1e+300`, not
  `1.0000000000000001E+300`) and a `decimal` without trailing zeros (`1.0m` prints `1`). Values
  still round-trip.
- **Number parsing** ignores `NumberStyles`/culture (always invariant); `As*` accessors that take a
  `cultureInfo` ignore it.
- A Fable Python codegen issue (a unit `()` arm in a multi-case `match` is dropped) is worked around
  in `parseBlockScalar`; avoid that shape in new code.

## Fixes the Fable runs exposed (also in the .NET build)

- Timestamps are built from components (`DateTimeOffset(y, m, d, h, mi, s, offset)` + `AddTicks`),
  because Fable read `DateTimeOffset(DateTime Unspecified, offset)` as local time (`07:08:09Z`
  became `05:08:09`). Calendar-invalid values (`2024-02-30`, hour 25) are rejected up front on every
  platform.
- A leading `+` (`+5`) is accepted by number parsing on every platform.
- `BigInteger.Pow`, `Char.ConvertFromUtf32`, `Uri.IsHexDigit` and `String.CompareOrdinal` are no longer
  used, and a huge exponent such as `1e999999` no longer builds a giant power of ten.
- Backslashes in `///` comments were removed (they produced invalid Python docstrings).
