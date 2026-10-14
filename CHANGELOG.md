# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

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
