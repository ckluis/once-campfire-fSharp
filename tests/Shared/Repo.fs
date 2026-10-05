/// Locations shared by every test project: the repository root, golden vectors and the parity seed.
module Campfire.Tests.Repo

open System
open System.IO
open System.Text.Json

let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (CampfireFs.slnx) not found"
        | dir when File.Exists(Path.Combine(dir.FullName, "CampfireFs.slnx")) -> dir.FullName
        | dir -> up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let path (relative: string) = Path.Combine(root, relative)

/// A golden vector file from vectors/, e.g. `vector "ruby_core"`.
let vector (name: string) : JsonDocument =
    JsonDocument.Parse(File.ReadAllText(path $"vectors/{name}.json"))

/// The parity seed directory, or None when it hasn't been built (parity/bin/seed build).
/// With CAMPFIRE_REQUIRE_SEED=1 a missing seed is a failure rather than a skip.
let seed (name: string) : string option =
    let dir = Environment.GetEnvironmentVariable "PARITY_SEED_DIR" |> Option.ofObj |> Option.defaultValue (path "parity/.seed")
    let d = Path.Combine(dir, name)
    if File.Exists(Path.Combine(d, "db", "production.sqlite3")) then Some d
    elif Environment.GetEnvironmentVariable "CAMPFIRE_REQUIRE_SEED" = "1" then failwith $"seed {name} not built under {dir}"
    else None
