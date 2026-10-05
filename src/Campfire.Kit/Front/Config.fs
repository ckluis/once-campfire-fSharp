// Port of rust/crates/kit/src/front/config.rs
//
// Thruster's configuration (internal/config.go): every setting is read from `THRUSTER_<NAME>`,
// falling back to `<NAME>`, and a value that doesn't parse falls back to the default.
namespace Campfire.Kit

open System
open System.Globalization
open System.Net
open Campfire.Kit

type FrontConfig =
    {
        /// `TARGET_PORT`: where Thruster's upstream listened. Thruster exported it to the app as `PORT`,
        /// so the app itself still listens there.
        TargetPort: int
        /// `TARGET_BIND` (not Thruster's): the address the app's own listener on TARGET_PORT binds.
        /// Loopback by default, since nothing outside needs it and it lacks the front's protections.
        TargetBind: IPAddress
        /// `CACHE_SIZE`: the response cache's capacity in bytes.
        CacheSize: int64
        /// `MAX_CACHE_ITEM_SIZE`: the largest response the cache stores.
        MaxCacheItemSize: int64
        GzipCompressionEnabled: bool
        GzipCompressionDisableOnAuth: bool
        GzipCompressionJitter: int64
        /// `MAX_REQUEST_BODY`: 0 for no limit.
        MaxRequestBody: int64
        /// `TLS_DOMAIN`, comma-separated. Setting it turns on TLS.
        TlsDomains: string list
        /// `ACME_DIRECTORY`
        AcmeDirectoryUrl: string
        EabKid: string
        EabHmacKey: string
        /// `STORAGE_PATH`: autocert's certificate cache directory.
        StoragePath: string
        HttpPort: int
        HttpsPort: int
        HttpIdleTimeout: TimeSpan
        HttpReadTimeout: TimeSpan
        HttpWriteTimeout: TimeSpan
        /// `H2C_ENABLED`: HTTP/2 without TLS (prior knowledge).
        H2cEnabled: bool
        /// `FORWARD_HEADERS`: trust the client's `X-Forwarded-*` (default: only without TLS).
        ForwardHeaders: bool
        /// `DEBUG`
        Debug: bool
        /// `LOG_REQUESTS`
        LogRequests: bool
    }

    member this.HasTls = not this.TlsDomains.IsEmpty

module FrontConfig =
    [<Literal>]
    let private MB = 1048576L

    /// Let's Encrypt's production directory (`acme.LetsEncryptURL`).
    [<Literal>]
    let LetsEncryptUrl = "https://acme-v02.api.letsencrypt.org/directory"

    /// Go's `strconv.ParseBool`.
    let private parseBool (value: string) : bool voption =
        match value with
        | "1"
        | "t"
        | "T"
        | "TRUE"
        | "true"
        | "True" -> ValueSome true
        | "0"
        | "f"
        | "F"
        | "FALSE"
        | "false"
        | "False" -> ValueSome false
        | _ -> ValueNone

    /// `strconv.Atoi`: an optional sign and decimal digits, nothing else.
    let private parseInt (value: string) : int64 voption =
        match Int64.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, n -> ValueSome n
        | _ -> ValueNone

    /// Rust's `IpAddr::from_str`: four decimal octets without leading zeros, or an IPv6 address.
    let private parseIp (value: string) : IPAddress voption =
        if value.Contains ':' then
            if value.Contains '%' || value.Contains '[' then
                ValueNone
            else
                match IPAddress.TryParse value with
                | true, ip ->
                    match ip with
                    | null -> ValueNone
                    | ip -> if ip.AddressFamily = Sockets.AddressFamily.InterNetworkV6 then ValueSome ip else ValueNone
                | _ -> ValueNone
        else
            let octets = value.Split '.'
            let valid (octet: string) =
                octet.Length >= 1
                && octet.Length <= 3
                && Seq.forall Char.IsAsciiDigit octet
                && (octet.Length = 1 || octet[0] <> '0')
                && int octet <= 255
            if octets.Length = 4 && Array.forall valid octets then
                ValueSome(IPAddress(octets |> Array.map byte))
            else
                ValueNone

    /// `NewConfig`, over any variable lookup (tests pass a map).
    let fromLookup (get: string -> string | null) : FrontConfig =
        let find (key: string) : string voption =
            match get ("THRUSTER_" + key) with
            | null ->
                match get key with
                | null -> ValueNone
                | v -> ValueSome v
            | v -> ValueSome v
        let string (key: string) (fallback: string) = find key |> ValueOption.defaultValue fallback
        let int (key: string) (fallback: int64) =
            find key |> ValueOption.bind parseInt |> ValueOption.defaultValue fallback
        let port (key: string) (fallback: int) =
            let n = int key (int64 fallback)
            if n >= 0L && n <= 65535L then Operators.int n else fallback
        let seconds (key: string) (fallback: int64) =
            match find key |> ValueOption.bind parseInt with
            | ValueSome s -> TimeSpan.FromSeconds(float (max s 0L))
            | ValueNone -> TimeSpan.FromSeconds(float fallback)
        let boolean (key: string) (fallback: bool) =
            find key |> ValueOption.bind parseBool |> ValueOption.defaultValue fallback
        let tlsDomains =
            match find "TLS_DOMAIN" with
            | ValueSome v -> v.Split ',' |> Array.map (fun d -> d.Trim()) |> Array.filter (fun d -> d <> "") |> List.ofArray
            | ValueNone -> []
        let config =
            { TargetPort = port "TARGET_PORT" 3000
              TargetBind = find "TARGET_BIND" |> ValueOption.bind parseIp |> ValueOption.defaultValue IPAddress.Loopback
              CacheSize = int "CACHE_SIZE" (64L * MB)
              MaxCacheItemSize = int "MAX_CACHE_ITEM_SIZE" MB
              GzipCompressionEnabled = boolean "GZIP_COMPRESSION_ENABLED" true
              GzipCompressionDisableOnAuth = boolean "GZIP_COMPRESSION_DISABLE_ON_AUTH" false
              GzipCompressionJitter = int "GZIP_COMPRESSION_JITTER" 32L
              MaxRequestBody = int "MAX_REQUEST_BODY" 0L
              TlsDomains = tlsDomains
              AcmeDirectoryUrl = string "ACME_DIRECTORY" LetsEncryptUrl
              EabKid = string "EAB_KID" ""
              EabHmacKey = string "EAB_HMAC_KEY" ""
              StoragePath = string "STORAGE_PATH" "./storage/thruster"
              HttpPort = port "HTTP_PORT" 80
              HttpsPort = port "HTTPS_PORT" 443
              HttpIdleTimeout = seconds "HTTP_IDLE_TIMEOUT" 60L
              HttpReadTimeout = seconds "HTTP_READ_TIMEOUT" 30L
              HttpWriteTimeout = seconds "HTTP_WRITE_TIMEOUT" 30L
              H2cEnabled = boolean "H2C_ENABLED" false
              ForwardHeaders = false
              Debug = boolean "DEBUG" false
              LogRequests = boolean "LOG_REQUESTS" true }
        { config with ForwardHeaders = boolean "FORWARD_HEADERS" (not config.HasTls) }

    let fromEnv () : FrontConfig = fromLookup Environment.GetEnvironmentVariable
