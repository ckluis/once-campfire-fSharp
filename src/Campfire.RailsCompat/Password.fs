// Port of rust/crates/rails_compat/src/password.rs
/// `has_secure_password` with `ActiveModel::SecurePassword::BCryptPassword`: bcrypt-ruby's
/// `$2a$` digests at `BCrypt::Engine.cost` (12). Like bcrypt-ruby, only the first 72 bytes of a
/// password count.
module Campfire.RailsCompat.Password

/// What `digestWithCost` can refuse (the Rust `BcryptError`).
type BcryptError = CostNotAllowed of cost: int

/// `BCrypt::Engine.cost` in production.
[<Literal>]
let Cost = 12

/// `BCrypt::Engine::MIN_COST`, what Rails uses when `ActiveModel::SecurePassword.min_cost` is set (tests).
[<Literal>]
let MinCost = 4

/// `BCrypt::Password.create(password, cost:)`. Fails for a cost outside `MinCost..=31`.
let digestWithCost (password: string) (cost: int) : Result<string, BcryptError> =
    if cost < MinCost || cost > 31 then
        Error(CostNotAllowed cost)
    else
        Ok(BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(cost, 'a')))

/// `BCrypt::Password.new(digest).is_password?(password)`; `false` for a malformed digest.
let verify (password: string) (digest: string) : bool =
    try
        BCrypt.Net.BCrypt.Verify(password, digest)
    with _ ->
        false
