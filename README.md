# Campfire in F#

An F# implementation of [ONCE Campfire](https://github.com/basecamp/once-campfire), built on
ASP.NET Core and Falco. It aims to be a drop-in replacement for the Rails app: the same SQLite
database, storage layout and signed or encrypted cookies, with the same pages and behavior.

**Status: under construction.** See [`plans/fsharp-port.md`](plans/fsharp-port.md) for the phases.

It is translated from the [Rust port](https://github.com/basecamp/once-campfire-rust) (pinned in
`rust/`), which reproduces the Rails app (pinned in `reference/`) and ships the parity harness,
golden vectors and benchmark tooling this repository reuses under the MIT license.

The Rails frontend ships with a few
[port-owned overrides](src/Campfire.Assets/OVERRIDES.md): three JavaScript files and one image that
deliberately differ from Rails.

## Development

```sh
git submodule update --init
bin/verify          # Release build with warnings as errors, then every test
```

See [`AGENTS.md`](AGENTS.md) for the layout and working rules.

## Known differences

The differences the Rust port lists from Rails apply here too (see its README). Differences
specific to this port are listed below, each citing the reference file it departs from.

- None yet.

## License

MIT. Campfire, the Rust port, its parity harness and vectors are © 37signals, LLC.
