// Port of rust/crates/kit/src/server.rs
//
// Process-level serving helpers: graceful shutdown on SIGINT/SIGTERM, and the open-file limit
// many sockets need. The server itself is `front`.
namespace Campfire.Kit

open System
open System.Runtime.InteropServices
open System.Threading.Tasks

[<Struct; StructLayout(LayoutKind.Sequential)>]
type internal RLimit =
    val mutable Cur: uint64
    val mutable Max: uint64

module internal Native =
    [<DllImport("libc.so.6", EntryPoint = "getrlimit")>]
    extern int getrlimitLinux(int resource, RLimit& limit)

    [<DllImport("libc.so.6", EntryPoint = "setrlimit")>]
    extern int setrlimitLinux(int resource, RLimit& limit)

    [<DllImport("libSystem.dylib", EntryPoint = "getrlimit")>]
    extern int getrlimitMac(int resource, RLimit& limit)

    [<DllImport("libSystem.dylib", EntryPoint = "setrlimit")>]
    extern int setrlimitMac(int resource, RLimit& limit)

module Server =
    /// RLIMIT_NOFILE
    let private resource = if OperatingSystem.IsMacOS() then 8 else 7

    let private getLimit (limit: byref<RLimit>) : int =
        if OperatingSystem.IsMacOS() then Native.getrlimitMac (resource, &limit) else Native.getrlimitLinux (resource, &limit)

    let private setLimit (limit: byref<RLimit>) : int =
        if OperatingSystem.IsMacOS() then Native.setrlimitMac (resource, &limit) else Native.setrlimitLinux (resource, &limit)

    /// Resolves on Ctrl-C or SIGTERM (what `kamal`/Docker send on stop).
    let shutdownSignal () : Task =
        let completion = TaskCompletionSource()
        let registrations = ResizeArray<PosixSignalRegistration>()
        let handler (context: PosixSignalContext) =
            // Stay alive until the host has shut down gracefully.
            context.Cancel <- true
            completion.TrySetResult() |> ignore
        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, handler))
        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, handler))
        completion.Task.ContinueWith(fun (_: Task) -> for r in registrations do r.Dispose())

    /// Raises the soft limit on open files to the hard limit, and returns the new limit. Every
    /// WebSocket is a file descriptor, and containers commonly start processes with a soft limit
    /// (65,536 in Docker's default, 1,024 elsewhere) far below the hard one, which would cap the
    /// number of connected clients however little memory each takes.
    let raiseOpenFileLimit () : uint64 voption =
        if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
            ValueNone
        else
            let mutable limit = RLimit()
            if getLimit &limit <> 0 then
                ValueNone
            else
                if limit.Cur < limit.Max then
                    let mutable raised = RLimit()
                    raised.Cur <- limit.Max
                    raised.Max <- limit.Max
                    if setLimit &raised = 0 then limit <- raised
                ValueSome limit.Cur

    /// The soft and hard open-file limits as the OS reports them now, for tests.
    let internal openFileLimits () : struct (uint64 * uint64) voption =
        let mutable limit = RLimit()
        if getLimit &limit = 0 then ValueSome(struct (limit.Cur, limit.Max)) else ValueNone
