// Port of rust/crates/campfire/src/main.rs
//
// The Campfire server: controllers, channels, jobs and integrations wired over the libraries
// (`Boot.run` is app.rs's entry point). Rust's jemalloc options and `transparent_huge_pages` opt-out
// become the GC settings in the project file and `disableTransparentHugePages` here.
module Campfire.App.Program

open System
open System.Runtime.InteropServices

module private Native =
    [<DllImport("libc.so.6", EntryPoint = "prctl")>]
    extern int prctl(int option, uint64 arg2, uint64 arg3, uint64 arg4, uint64 arg5)

/// `PR_SET_THP_DISABLE` and `PR_GET_THP_DISABLE` (linux/prctl.h).
[<Literal>]
let private PrSetThpDisable = 41

[<Literal>]
let private PrGetThpDisable = 42

/// On kernels with transparent huge pages set to `always` (Debian's and Arch's default), every thread's
/// stack and each of the allocator's regions get backed by whole 2 MB pages as soon as they're touched: an
/// idle server took 160 MB on 32 cores instead of 15 MB. Nothing here is big enough to gain from huge
/// pages, so the process (and ffmpeg, which inherits it) opts out.
let disableTransparentHugePages () : unit =
    if OperatingSystem.IsLinux() then
        if Native.prctl (PrSetThpDisable, 1UL, 0UL, 0UL, 0UL) <> 0 then
            eprintfn "couldn't disable transparent huge pages: %s" (Marshal.GetLastPInvokeErrorMessage())

/// Whether `disableTransparentHugePages` took (Linux only).
let transparentHugePagesDisabled () : bool = Native.prctl (PrGetThpDisable, 0UL, 0UL, 0UL, 0UL) = 1

[<EntryPoint>]
let main argv =
    disableTransparentHugePages ()
    Boot.run argv
