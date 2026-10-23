# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-10-23

### Added

- Fable support: the library compiles to JavaScript and Python from the same source (`#if FABLE_COMPILER` branches in a new internal `YamlPortable` module; the file/stream/URL loaders and `WriteTo(TextWriter)` are compiled out under Fable only). The shared test suite runs under Fable JS and Python via `scripts/fable-test.sh` (Fable 5.21.0 pinned in `.config/dotnet-tools.json`, generated runner in `scripts/fable-gen-runner.fsx`); see `docs/fable.md` for prerequisites and known differences. The .NET public API and behaviour are unchanged.
- Original timestamp text: `YamlValue.ParseWithMetadata` (plus `TryParseWithMetadata` and `ParseMultipleWithMetadata`) returns a `YamlParsed` with the root value, the spans, and `TimestampText`, a map from JSON Pointer to the source text of each `Timestamp` (`2026-10-14` stays a date, a zoneless date-time stays zoneless). The `YamlValue` union and `Parse` are unchanged and still hold only the `DateTimeOffset`.
- Source positions: `YamlValue.ParseWithPositions`, `TryParseWithPositions` and `ParseMultipleWithPositions` return a `Map<string, Span>` from RFC 6901 JSON Pointer (over the expanded tree) to the source span of every node, with new `Pos`, `Span` and `YamlParseError` types. Aliases and merged-in keys are positioned at the alias / `<<` site. `Parse` and its performance are unchanged; see `docs/reference.md`.

### Fixed

- Portability fixes found by running under Fable: timestamps are built from components (Fable read `DateTimeOffset(DateTime, offset)` as local time, turning `07:08:09Z` into `05:08:09`), calendar-invalid timestamp-shaped text (`2024-02-30`, hour 25) is rejected up front, and `+5` parses under Fable. A huge exponent such as `1e999999` no longer builds a giant power of ten when deciding decimal-exactness. On .NET results are unchanged.
- Every finite `Float` now round-trips as `Float`. A float whose plain text would resolve to `Number` (not just integral ones, but also decimal-exact values such as `0.5`, `1.5` or `0.1`, which emits as `0.10000000000000001`) is emitted with an explicit `!!float` tag, e.g. `!!float 1.5`. Floats whose plain text already re-parses as `Float` (e.g. `1.0000000000000001E+300`) are emitted exactly as before. The FsCheck generator no longer filters out these values.
- Integral `Float` values (`1.0`, `-0.0`, `1e15`…`1e17`, …) now round-trip as `Float`. Previously they were emitted as `1`/`-0`/`1000000000000000` and re-parsed as `Number`; since `1.0` itself also parses as `Number` (it is exactly `decimal`-representable), they are now emitted with an explicit tag, e.g. `!!float 1.0` / `!!float -0.0`. Such a value used as a block-mapping key is emitted in explicit `? key` form, and the parser accepts a tagged scalar after `?`. Non-integral floats and `Number` output are unchanged.
- Strings `"..."` and those starting with `"... "` are now quoted on emit so they are not re-read as a document end marker (`"---"` and `"--- "…` were already quoted via the leading-indicator rule; regression tests now cover them at the root and as keys/values).
- The string `"<<"` is now quoted on emit, and a quoted `"<<"` mapping key in the source is treated as a literal key rather than a merge key (only the plain `<<` key merges), so `Parse(v.ToString())` round-trips a `"<<"` key instead of merging or dropping it.
- Double-quoted scalars now accept JSON-style UTF-16 surrogate-pair escapes (e.g. `"\uD83C\uDF89"` parses to U+1F389) instead of failing with "Invalid Unicode code point". A lone/unpaired surrogate escape is still an error.

## [0.1.1] - 2026-09-12

### Fixed

- Replaced `sprintf`/`printf` usage across the parser, emitter, and path DSL with plain string concatenation. F#'s printf machinery builds format functions via reflection, which crashed under NativeAOT on any malformed YAML input (or on `ToString()`/`_Print`); the library is now verified to work in a NativeAOT-published app.

### Added

- `IsAotCompatible` set on the library project.

## [0.1.0] - 2026-09-11

- Initial release of `FSharp.Data.YamlValue`: `YamlValue` DU, `Parse`/`Load`, `?` dynamic operator, `As*` accessors, round-tripping `ToString()`, anchors/aliases, multi-document streams, `YamlDocument` with comment round-tripping, and `GetPath`/`TryGetPath`/`SetPath` builders.
