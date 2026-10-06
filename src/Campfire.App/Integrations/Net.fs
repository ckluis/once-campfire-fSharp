// Port of rust/crates/campfire/src/integrations/net.rs
//
// What the three HTTP clients share: name resolution, connecting (to a pinned address when a policy
// requires one), TLS, and a small HTTP/1.1 exchange (`Http.fs`) that behaves like `Net::HTTP`. Each client
// keeps its own policy (see plans/rust-conversion.md, "HTTP clients: three distinct policies"); none of them
// uses a proxy.
namespace Campfire.App.Integrations

open System
open System.Net
open System.Net.Sockets
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks

/// Name resolution. The system one is `getaddrinfo` (Ruby's `Resolv.getaddresses` reads /etc/hosts,
/// then DNS); tests substitute fixed answers.
type IResolver =
    abstract Lookup: host: string -> Task<Result<IPAddress list, exn>>

/// Opens the TCP connection to an address. Tests redirect fake public addresses to a local server;
/// everything else connects for real. A failure is the `SocketException` the connect raised.
type IDialer =
    abstract Connect: address: IPEndPoint * cancellation: CancellationToken -> Task<Socket>

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

[<Sealed>]
type TcpDialer() =
    interface IDialer with
        member _.Connect(address: IPEndPoint, cancellation: CancellationToken) : Task<Socket> =
            task {
                let socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                try
                    socket.NoDelay <- true
                    do! socket.ConnectAsync(address, cancellation)
                    return socket
                with e ->
                    socket.Dispose()
                    return raise e
            }

/// Which certificate authorities a TLS peer is verified against.
type TlsTrust =
    /// The system's CA certificates, which `Net::HTTP` verifies peers against (OpenSSL's default store).
    | SystemRoots
    /// Only these (the tests' CA, or none).
    | CustomRoots of X509Certificate2Collection

/// The resolver, dialer and TLS trust the clients use. `Network.system` in production.
type Network =
    { Resolver: IResolver
      Dialer: IDialer
      Tls: TlsTrust }

module Network =
    /// The system resolver, real connections, and the system's CA certificates.
    let system () : Network =
        { Resolver = SystemResolver()
          Dialer = TcpDialer()
          Tls = SystemRoots }
