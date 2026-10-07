// Port of the #[cfg(all(test, unix))] module in rust/crates/kit/src/server.rs
module Campfire.Kit.Tests.ServerTests

open System
open System.Threading.Tasks
open Xunit
open Campfire.Kit

[<Fact>]
let ``raises the soft limit to the hard one`` () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        ()
    else
        let before = (Server.openFileLimits ()).Value
        let limit = (Server.raiseOpenFileLimit ()).Value
        let struct (soft, hard) = (Server.openFileLimits ()).Value
        let struct (softBefore, _) = before
        Assert.Equal(limit, soft)
        Assert.True(soft >= softBefore)
        // macOS refuses a soft limit past `kern.maxfilesperproc`, and its hard limit is "unlimited".
        if OperatingSystem.IsLinux() then Assert.Equal(hard, soft)

[<Fact>]
let ``the shutdown signal is a task that is not done until a signal comes`` () =
    let signal = Server.shutdownSignal ()
    Assert.False signal.IsCompleted
