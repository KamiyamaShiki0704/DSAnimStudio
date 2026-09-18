# FXR decode runtime

This directory contains Node.js 24.19.0 (Windows x64), @cccode/fxr 32.1.0,
their licenses, and the DSA decode bridge. Both runtime and parser are local;
no installation, network request or game-memory attachment is needed at runtime.

The renderer requests original FXR bytes from the selected game/mod sfx directory.
The bridge accepts those bytes on stdin, explicitly selects the game's format,
and returns decoded JSON. It is a structural parser, not the game's VFX engine.
The process exits after one decode. DSA limits concurrent decoding to two jobs,
with a 30-second decode timeout and cancellation when resources are reloaded.

Source: https://github.com/EvenTorset/fxr (Unlicense).
Node license: https://github.com/nodejs/node/blob/v24.19.0/LICENSE.
`fxr.mjs` is the package's bundled `dist/fxr.js`, renamed for explicit ESM loading.
Game FXR binaries, textures, models and game executables are not included.

Decoded JSON is cached in `%LOCALAPPDATA%/DSAnimStudio/FxrCache/32.1.0-v1/`,
keyed by game and source SHA256. Reloading resources re-reads source bytes and
textures/models; changed FXR files automatically get a different cache entry.
See PreviewGuide.md for rendering limitations and trigger coverage.
