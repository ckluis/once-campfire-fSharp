// The counterpart of the `embedded` module in rust/crates/assets/src/lib.rs
/// What Campfire.Assets.Build generated: the sorted tables (EmbeddedData.fs) and the bytes of
/// every servable file, in one embedded resource that is used where it sits in the assembly's
/// mapping, never copied.
module internal Campfire.Assets.Embedded

#nowarn "9" // the resource is addressed through a pointer
#nowarn "3391" // Memory<byte> to ReadOnlyMemory<byte>

open System
open System.Buffers
open System.IO
open System.Reflection
open System.Text
open Microsoft.FSharp.NativeInterop

/// A `Memory<byte>` over native memory that outlives the process's use of it.
type private NativeMemory(pointer: nativeint, length: int) =
    inherit MemoryManager<byte>()
    override _.GetSpan() = Span<byte>(pointer.ToPointer(), length)
    override _.Pin(elementIndex: int) = new MemoryHandle(NativePtr.toVoidPtr (NativePtr.add (NativePtr.ofNativeInt<byte> pointer) elementIndex))
    override _.Unpin() = ()
    override _.Dispose(_disposing: bool) = ()

let private resource (name: string) : Stream =
    match Assembly.GetExecutingAssembly().GetManifestResourceStream name with
    | null -> failwith $"embedded resource {name} is missing; Campfire.Assets.Build didn't run"
    | stream -> stream

/// assets.bin. Embedded resources are an UnmanagedMemoryStream over the mapped assembly.
let private blob: ReadOnlyMemory<byte> =
    match resource "assets.bin" with
    | :? UnmanagedMemoryStream as stream -> (new NativeMemory(NativePtr.toNativeInt stream.PositionPointer, int stream.Length)).Memory
    | stream ->
        use stream = stream
        use copy = new MemoryStream()
        stream.CopyTo copy
        ReadOnlyMemory(copy.ToArray())

let manifest: (string * string)[] = EmbeddedData.manifest

let stylesheets: string[] = EmbeddedData.stylesheets

let builtAt: string = EmbeddedData.builtAt

/// A binary search over a table sorted by ordinal comparison of its keys; -1 when absent.
let private search (length: int) (key: int -> string) (target: string) : int =
    let mutable low = 0
    let mutable high = length - 1
    let mutable found = -1
    while found < 0 && low <= high do
        let mid = low + (high - low) / 2
        match String.CompareOrdinal(key mid, target) with
        | 0 -> found <- mid
        | c when c < 0 -> low <- mid + 1
        | _ -> high <- mid - 1
    found

/// The digested path for a logical path, from the sorted manifest.
let digestedPath (logicalPath: string) : string voption =
    match search manifest.Length (fun i -> fst manifest[i]) logicalPath with
    | -1 -> ValueNone
    | i -> ValueSome(snd manifest[i])

/// The bytes served at a URL path, from the sorted file table.
let file (url: string) : ReadOnlyMemory<byte> voption =
    let files = EmbeddedData.files
    match search files.Length (fun i -> let struct (u, _, _) = files[i] in u) url with
    | -1 -> ValueNone
    | i ->
        let struct (_, offset, length) = files[i]
        ValueSome(blob.Slice(offset, length))

let private text (name: string) : string =
    use reader = new StreamReader(resource name, UTF8Encoding false)
    reader.ReadToEnd()

/// Propshaft's manifest, as served from `/assets/.manifest.json`.
let manifestJson: Lazy<string> =
    lazy
        (match file "/assets/.manifest.json" with
         | ValueSome body -> Encoding.UTF8.GetString body.Span
         | ValueNone -> failwith "the manifest is missing from assets.bin")

let importmapTags: Lazy<string> = lazy (text "importmap-tags.html")
