// Port of the types of rust/crates/richtext/vendor/html5ever (markup5ever's QualName and TagAttribute,
// the tokenizer's Tag, the tree builder's TreeSink).
//
// html5ever is vendored in Rust at 0.35 with a backported fix and two additions for Gumbo's limits;
// see vendor/html5ever/Cargo.toml. This is the same parser in F#, because AngleSharp can neither
// stop at Gumbo's depth limit while it builds the tree nor count a tag's attributes as the
// tokenizer reads it, and parse equivalence with Gumbo is what the corpus tests pin.
namespace Campfire.RichText.Html5ever

module internal Strings =
    /// Rust's `char::is_ascii_whitespace`: space, tab, line feed, form feed and carriage return.
    let inline isAsciiWhitespace (c: char) = c = ' ' || c = '\t' || c = '\n' || c = '\012' || c = '\r'

/// The namespaces the tree builder assigns (`ns!()`, html, xml, xmlns, xlink, svg, mathml).
type Ns =
    | Empty
    | Html
    | Xml
    | Xmlns
    | Xlink
    | Svg
    | MathMl

/// markup5ever's `QualName`. `Prefix` is null for no prefix, which is not the same as the empty
/// prefix the tree builder gives `xmlns` (`namespace_prefix!("")`).
[<NoComparison; CustomEquality>]
type QualName =
    { Prefix: string | null
      Ns: Ns
      Local: string }

    override this.Equals(other: obj) =
        match other with
        | :? QualName as other -> this.Ns = other.Ns && this.Local = other.Local && this.Prefix = other.Prefix
        | _ -> false

    override this.GetHashCode() = hash this.Local

module QualName =
    let create (prefix: string | null) (ns: Ns) (local: string) : QualName = { Prefix = prefix; Ns = ns; Local = local }

    /// `QualName::new(None, ns, local)`.
    let unprefixed (ns: Ns) (local: string) : QualName = { Prefix = null; Ns = ns; Local = local }

    /// The name as Nokogiri reports and serializes it ("xlink:href", "href").
    let qualified (name: QualName) : string =
        match name.Prefix with
        | null -> name.Local
        | prefix -> prefix + ":" + name.Local

type TagAttribute = { Name: QualName; Value: string }

type TagKind =
    | StartTag
    | EndTag

type Tag =
    { Kind: TagKind
      Name: string
      SelfClosing: bool
      Attrs: TagAttribute[] }

/// What `TreeSink::append` and friends take: a node, or text that merges into a preceding text node.
[<Struct>]
type NodeOrText =
    | AppendNode of node: int
    | AppendText of text: string

/// markup5ever's `TreeSink`, with the methods whose defaults the DOM doesn't override left out
/// (parse errors, quirks mode, doctypes, form association, script flags, line numbers). Handles
/// are node ids.
type ITreeSink =
    abstract Document: int
    abstract ElemName: handle: int -> QualName
    abstract CreateElement: name: QualName * attrs: TagAttribute[] -> int
    abstract CreateComment: text: string -> int
    abstract Append: parent: int * child: NodeOrText -> unit
    abstract AppendBasedOnParentNode: element: int * prevElement: int * child: NodeOrText -> unit
    /// `get_template_contents`: libxml2 has no template contents, so they are the element's children.
    abstract GetTemplateContents: target: int -> int
    abstract AppendBeforeSibling: sibling: int * newNode: NodeOrText -> unit
    abstract AddAttrsIfMissing: target: int * attrs: TagAttribute[] -> unit
    abstract RemoveFromParent: target: int -> unit
    abstract ReparentChildren: node: int * newParent: int -> unit
