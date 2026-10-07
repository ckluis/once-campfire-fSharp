// Port of rust/crates/storage/src/vips.rs
//
// Rust declares the libvips calls ruby-vips makes for Active Storage by hand and calls them over
// FFI; here NetVips (the managed binding, over the system libvips: no NetVips.Native) makes the
// same operations with the same options. libvips must be the same version as the reference
// image's for variants to be byte-identical.
namespace Campfire.Storage

open System
open System.Reflection
open System.Runtime.InteropServices

module private Native =
    // `NetVips.NetVips` has no wrapper for `vips_operation_block_set` or `vips_error_clear`, so
    // they are imported here and resolved to the same library NetVips loads (see `resolver`).
    [<DllImport("vips", CallingConvention = CallingConvention.Cdecl)>]
    extern void vips_operation_block_set([<MarshalAs(UnmanagedType.LPStr)>] string name, int state)

    [<DllImport("vips", CallingConvention = CallingConvention.Cdecl)>]
    extern void vips_error_clear()

    /// The library names NetVips tries, in its order of platforms.
    let private candidates : string list =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then [ "libvips-42.dll" ]
        elif RuntimeInformation.IsOSPlatform OSPlatform.OSX then [ "libvips.42.dylib" ]
        else [ "libvips.so.42" ]

    let resolver : DllImportResolver =
        DllImportResolver(fun name assembly searchPath ->
            if name <> "vips" then
                IntPtr.Zero
            else
                candidates
                |> List.tryPick (fun candidate ->
                    match NativeLibrary.TryLoad(candidate, assembly, searchPath) with
                    | true, handle -> Some handle
                    | _ -> None)
                |> Option.defaultValue IntPtr.Zero)

module Vips =
    /// `vips_init`, then the loader restrictions of `config/initializers/vips.rb`:
    /// `Vips.block_untrusted(true)` and `Vips.block("VipsForeignLoadOpenslide", true)`. Runs on
    /// first use rather than at boot, so a server that never touches an image doesn't pay for
    /// libvips. A failure is kept and returned every time.
    let private initialization : Lazy<Result<unit, string>> =
        lazy
            (try
                NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Native.resolver)
                // NetVips initializes libvips when it is first used.
                NetVips.NetVips.BlockUntrusted <- true
                Native.vips_operation_block_set ("VipsForeignLoadOpenslide", 1)
                Ok()
             with e ->
                 Error $"vips_init failed: {e.Message}")

    let init () : StorageResult<unit> =
        match initialization.Value with
        | Ok() -> Ok()
        | Error message -> Error(StorageError.Vips message)

    let version () : StorageResult<string> =
        init ()
        |> Result.bind (fun () ->
            try
                Ok $"{NetVips.NetVips.Version(0, false)}.{NetVips.NetVips.Version(1, false)}.{NetVips.NetVips.Version(2, false)}"
            with e ->
                Error(StorageError.Vips e.Message))

    /// libvips' error text for a failed call. The error buffer is process-wide, and NetVips takes
    /// it into the exception it raises; anything it leaves behind is cleared.
    let private guard (f: unit -> 'a) : StorageResult<'a> =
        try
            Ok(f ())
        with :? NetVips.VipsException as e ->
            Native.vips_error_clear ()
            Error(StorageError.Vips(e.Message.TrimEnd()))

    /// Whether the loader libvips picks for `path` has an optional `page` input, as
    /// `Utils.select_valid_loader_options` asks ruby-vips' `Introspect`. NetVips' `Introspect` reads
    /// the whole argument table, like ruby-vips', so no "no property named `page'" lands in
    /// libvips' error buffer for jpegload or pngload.
    let internal loaderAcceptsPage (path: string) : bool =
        try
            match NetVips.Image.FindLoad path with
            | null ->
                Native.vips_error_clear ()
                false
            | loader -> (NetVips.Introspect.Get loader).OptionalInput.ContainsKey "page"
        with :? NetVips.VipsException ->
            Native.vips_error_clear ()
            false

    /// `ImageProcessing::Vips::Processor::SHARPEN_MASK`: `new_from_array([[-1,-1,-1],[-1,32,-1],[-1,-1,-1]], 24)`.
    let private sharpenMask () : NetVips.Image =
        NetVips.Image.NewFromArray(array2D [ [ -1.0; -1.0; -1.0 ]; [ -1.0; 32.0; -1.0 ]; [ -1.0; -1.0; -1.0 ] ], 24.0, 0.0)

    /// One reference to a libvips image, released on dispose.
    [<Sealed>]
    type Image private (inner: NetVips.Image) =
        interface IDisposable with
            member _.Dispose() = inner.Dispose()

        member _.Width : int = inner.Width

        member _.Height : int = inner.Height

        /// `image.get(name)` for string fields such as `exif-ifd0-Orientation`; `None` when absent.
        member _.GetString(name: string) : string option =
            try
                if inner.GetTypeOf name = IntPtr.Zero then
                    None
                else
                    match inner.Get name with
                    | null -> None
                    | :? string as s -> Some s
                    | other -> Some(string other)
            with :? NetVips.VipsException ->
                Native.vips_error_clear ()
                None

        member _.Autorot() : StorageResult<Image> = guard (fun () -> new Image(inner.Autorot()))

        /// `resize_to_limit(width, height)`: `thumbnail_image(width, height:, size: :down,
        /// no_rotate: true)` followed by `conv(SHARPEN_MASK, precision: :integer)`.
        member _.ResizeToLimit(width: int option, height: int option) : StorageResult<Image> =
            let maxCoord = 10_000_000
            let width = defaultArg width maxCoord
            let height = defaultArg height maxCoord
            guard (fun () ->
                use thumbnail = inner.ThumbnailImage(width, height = height, size = NetVips.Enums.Size.Down, noRotate = true)
                use mask = sharpenMask ()
                new Image(thumbnail.Conv(mask, precision = NetVips.Enums.Precision.Integer)))

        /// `write_to_file(path)`: the saver and its defaults are picked from the extension.
        member _.WriteToFile(path: string) : StorageResult<unit> = guard (fun () -> inner.WriteToFile path)

        /// `Vips::Image.new_from_file(path, access: :sequential)`, as the image analyzer opens files.
        static member OpenSequential(path: string) : StorageResult<Image> =
            init ()
            |> Result.bind (fun () -> guard (fun () -> new Image(NetVips.Image.NewFromFile(path, access = NetVips.Enums.Access.Sequential))))

        /// `ImageProcessing::Vips::Processor.load_image(path, page: 0)`: `page: 0` only reaches
        /// loaders that accept it (`Utils.select_valid_loader_options`), then `autorot`.
        static member LoadForProcessing(path: string) : StorageResult<Image> =
            init ()
            |> Result.bind (fun () ->
                guard (fun () ->
                    if loaderAcceptsPage path then
                        let options = NetVips.VOption()
                        options.Add("page", 0)
                        NetVips.Image.NewFromFile(path, kwargs = options)
                    else
                        NetVips.Image.NewFromFile path)
                |> Result.map (fun loaded -> new Image(loaded)))
            |> Result.bind (fun image ->
                use image = image
                image.Autorot())
