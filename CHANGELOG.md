# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Replaced `sprintf`/`printf` usage across the parser, emitter, and path DSL with plain string concatenation. F#'s printf machinery builds format functions via reflection, which crashed under NativeAOT on any malformed YAML input (or on `ToString()`/`_Print`); the library is now verified to work in a NativeAOT-published app.

### Added

- `IsAotCompatible` set on the library project.

## [0.1.0] - 2026-09-11

- Initial release of `FSharp.Data.YamlValue`: `YamlValue` DU, `Parse`/`Load`, `?` dynamic operator, `As*` accessors, round-tripping `ToString()`, anchors/aliases, multi-document streams, `YamlDocument` with comment round-tripping, and `GetPath`/`TryGetPath`/`SetPath` builders.
