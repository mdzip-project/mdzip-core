# Upstream requests

## Baseline fixtures and conditional extension profiles

Requested 2026-10-25; scope revised to separate mandatory operations from extras.
Owner: `mdzip-spec`. Request: https://github.com/mdzip-project/mdzip-spec/issues/9

- Dependency: a versioned baseline R1-R7 contract/fixture schema and sync path is
  required for shared baseline acceptance and CI adapters. Local implementation
  and regression tests can proceed before the shared suite lands.
- Baseline API mapping: [#7](https://github.com/mdzip-project/mdzip-core/issues/7). Existing optional image-analysis bug: [#6](https://github.com/mdzip-project/mdzip-core/issues/6). Optional missing-image enhancement [#4](https://github.com/mdzip-project/mdzip-core/issues/4) is not a baseline blocker. Built-in ZIP-bomb limits ([#9](https://github.com/mdzip-project/mdzip-core/issues/9), R7) are local required work that doesn't wait on the suite; making them configurable (superseded #8) stays optional.
- Optional profiles are conditional on advertised capabilities. Unsupported is
  not applicable, not failed baseline parity. Do not wait for all libraries to
  implement all extensions; do not silently skip failing advertised behavior.
- Existing image analysis must pass its supported image-profile cases after the code-example bug is fixed. Adding a public extractor or typed exception/byte-stream overloads is not a prerequisite.
- Reference: hub `.github/docs/core-contract.md` (draft 0.1) and
  `.github/docs/core-parity.md`. Format spec requirements remain separate from
  first-party library operations and optional extension contracts.

Close the baseline request when the shared baseline adapter is running in CI.
Optional profile adoption follows its own supported capability/consumer work;
it is not a universal completion prerequisite.
