// Port of the name resolution part of rust/crates/campfire/src/integrations/net.rs
//
// What the three HTTP clients share: name resolution, connecting (to a pinned address when a policy
// requires one), TLS, and a small HTTP/1.1 exchange that behaves like `Net::HTTP`. Each client keeps
// its own policy (see plans/rust-conversion.md, "HTTP clients: three distinct policies"); none of
// them uses a proxy.
//
// Only the resolver is here so far: the push subscription controller validates endpoints through the
// private network guard (`Guard`), and the unfurl controller checks its URL the same way. The dialer,
// TLS trust and the HTTP exchange (`http.rs`) arrive with the integrations that use them
// (`Opengraph`, `WebPush`, webhooks); they are fields of `Network` then.
namespace Campfire.App.Integrations

open System
open System.Net
open System.Threading.Tasks

/// Name resolution. The system one is `getaddrinfo` (Ruby's `Resolv.getaddresses` reads /etc/hosts,
/// then DNS); tests substitute fixed answers.
type IResolver =
    abstract Lookup: host: string -> Task<Result<IPAddress list, exn>>

[<Sealed>]
type SystemResolver() =
    interface IResolver with
        member _.Lookup(host: string) : Task<Result<IPAddress list, exn>> =
            task {
                try
                    let! addresses = Dns.GetHostAddressesAsync host
                    return Ok(List.ofArray addresses)
                with e ->
                    return Error e
            }

/// The resolver (and, later, dialer and TLS trust) the clients use. `Network.system` in production.
type Network = { Resolver: IResolver }

module Network =
    /// The system resolver.
    let system () : Network = { Resolver = SystemResolver() }
