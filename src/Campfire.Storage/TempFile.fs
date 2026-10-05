// Stands in for the `tempfile` crate (`NamedTempFile`, `Builder` and `tempdir`), which
// rust/crates/storage uses for the copies it makes of blobs and for what it processes them into.
namespace Campfire.Storage

open System
open System.IO
open System.Security.Cryptography

module private TempNames =
    let private alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"

    /// `tempfile`'s six random characters.
    let randomPart () : string =
        String(Array.init 6 (fun _ -> alphanumeric[RandomNumberGenerator.GetInt32 alphanumeric.Length]))

    let ownerOnly = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

/// A file that is deleted when this is disposed (`NamedTempFile`).
[<Sealed>]
type TempFile private (path: string) =
    let mutable deleted = false

    member _.Path : string = path

    /// Creates `<temp dir>/<prefix><random><suffix>`, which didn't exist, readable by its owner only.
    static member Create(prefix: string, suffix: string) : TempFile =
        let rec attempt (tries: int) =
            let path = Path.Combine(Path.GetTempPath(), prefix + TempNames.randomPart () + suffix)
            try
                let options = FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = TempNames.ownerOnly)
                (new FileStream(path, options)).Dispose()
                new TempFile(path)
            with :? IOException when tries > 0 && File.Exists path ->
                attempt (tries - 1)
        attempt 16

    /// `NamedTempFile::new()`: the `.tmp` prefix `tempfile` uses by default.
    static member Create() : TempFile = TempFile.Create(".tmp", "")

    interface IDisposable with
        member _.Dispose() =
            if not deleted then
                deleted <- true
                try
                    File.Delete path
                with :? IOException | :? UnauthorizedAccessException ->
                    ()

/// A directory that is deleted with everything in it when this is disposed (`tempfile::tempdir()`).
[<Sealed>]
type TempDir private (path: string) =
    member _.Path : string = path

    static member Create() : TempDir =
        let rec attempt () =
            let path = Path.Combine(Path.GetTempPath(), ".tmp" + TempNames.randomPart ())
            if Directory.Exists path || File.Exists path then
                attempt ()
            else
                Directory.CreateDirectory(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
                new TempDir(path)
        attempt ()

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(path, true)
            with :? IOException | :? UnauthorizedAccessException ->
                ()
