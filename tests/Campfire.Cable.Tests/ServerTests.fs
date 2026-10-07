// Port of the tests in rust/crates/cable/src/server.rs
module Campfire.Cable.Tests.ServerTests

open Microsoft.AspNetCore.Http
open Xunit
open Campfire.Cable

let private headers (protocols: string) : IHeaderDictionary =
    let headers = HeaderDictionary()
    headers["Sec-WebSocket-Protocol"] <- protocols
    headers

[<Fact>]
let ``negotiation follows the clients order`` () =
    Assert.Equal(Some "actioncable-v1-json", Endpoint.negotiateProtocol (headers "actioncable-v1-json, actioncable-unsupported"))
    Assert.Equal(Some "actioncable-unsupported", Endpoint.negotiateProtocol (headers "actioncable-unsupported, actioncable-v1-json"))
    Assert.Equal(None, Endpoint.negotiateProtocol (headers "foo"))
    Assert.Equal(None, Endpoint.negotiateProtocol (HeaderDictionary()))
