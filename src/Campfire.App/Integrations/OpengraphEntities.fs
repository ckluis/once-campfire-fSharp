// Port of rust/crates/campfire/src/integrations/opengraph/entities.rs
//
// libxml2's `html40EntitiesTable`: the HTML 4.01 named character references plus `apos`, which its HTML parser
// decodes (names are case-sensitive and need their `;`). Sorted by name, in byte order, for a binary search.
namespace Campfire.App.Integrations

module OpengraphEntities =
    let Names: string[] =
        [|
            "AElig"; "Aacute"; "Acirc"; "Agrave"; "Alpha"; "Aring"; "Atilde"; "Auml"; "Beta"; "Ccedil"; "Chi";
            "Dagger"; "Delta"; "ETH"; "Eacute"; "Ecirc"; "Egrave"; "Epsilon"; "Eta"; "Euml"; "Gamma"; "Iacute";
            "Icirc"; "Igrave"; "Iota"; "Iuml"; "Kappa"; "Lambda"; "Mu"; "Ntilde"; "Nu"; "OElig"; "Oacute"; "Ocirc";
            "Ograve"; "Omega"; "Omicron"; "Oslash"; "Otilde"; "Ouml"; "Phi"; "Pi"; "Prime"; "Psi"; "Rho"; "Scaron";
            "Sigma"; "THORN"; "Tau"; "Theta"; "Uacute"; "Ucirc"; "Ugrave"; "Upsilon"; "Uuml"; "Xi"; "Yacute"; "Yuml";
            "Zeta"; "aacute"; "acirc"; "acute"; "aelig"; "agrave"; "alefsym"; "alpha"; "amp"; "and"; "ang"; "apos";
            "aring"; "asymp"; "atilde"; "auml"; "bdquo"; "beta"; "brvbar"; "bull"; "cap"; "ccedil"; "cedil"; "cent";
            "chi"; "circ"; "clubs"; "cong"; "copy"; "crarr"; "cup"; "curren"; "dArr"; "dagger"; "darr"; "deg"; "delta";
            "diams"; "divide"; "eacute"; "ecirc"; "egrave"; "empty"; "emsp"; "ensp"; "epsilon"; "equiv"; "eta"; "eth";
            "euml"; "euro"; "exist"; "fnof"; "forall"; "frac12"; "frac14"; "frac34"; "frasl"; "gamma"; "ge"; "gt";
            "hArr"; "harr"; "hearts"; "hellip"; "iacute"; "icirc"; "iexcl"; "igrave"; "image"; "infin"; "int"; "iota";
            "iquest"; "isin"; "iuml"; "kappa"; "lArr"; "lambda"; "lang"; "laquo"; "larr"; "lceil"; "ldquo"; "le";
            "lfloor"; "lowast"; "loz"; "lrm"; "lsaquo"; "lsquo"; "lt"; "macr"; "mdash"; "micro"; "middot"; "minus";
            "mu"; "nabla"; "nbsp"; "ndash"; "ne"; "ni"; "not"; "notin"; "nsub"; "ntilde"; "nu"; "oacute"; "ocirc";
            "oelig"; "ograve"; "oline"; "omega"; "omicron"; "oplus"; "or"; "ordf"; "ordm"; "oslash"; "otilde";
            "otimes"; "ouml"; "para"; "part"; "permil"; "perp"; "phi"; "pi"; "piv"; "plusmn"; "pound"; "prime"; "prod";
            "prop"; "psi"; "quot"; "rArr"; "radic"; "rang"; "raquo"; "rarr"; "rceil"; "rdquo"; "real"; "reg"; "rfloor";
            "rho"; "rlm"; "rsaquo"; "rsquo"; "sbquo"; "scaron"; "sdot"; "sect"; "shy"; "sigma"; "sigmaf"; "sim";
            "spades"; "sub"; "sube"; "sum"; "sup"; "sup1"; "sup2"; "sup3"; "supe"; "szlig"; "tau"; "there4"; "theta";
            "thetasym"; "thinsp"; "thorn"; "tilde"; "times"; "trade"; "uArr"; "uacute"; "uarr"; "ucirc"; "ugrave";
            "uml"; "upsih"; "upsilon"; "uuml"; "weierp"; "xi"; "yacute"; "yen"; "yuml"; "zeta"; "zwj"; "zwnj";
        |]

    /// The code point of the name at the same index in `Names`.
    let CodePoints: int[] =
        [|
            198; 193; 194; 192; 913; 197; 195; 196; 914; 199; 935; 8225; 916; 208; 201; 202; 200; 917; 919; 203; 915;
            205; 206; 204; 921; 207; 922; 923; 924; 209; 925; 338; 211; 212; 210; 937; 927; 216; 213; 214; 934; 928;
            8243; 936; 929; 352; 931; 222; 932; 920; 218; 219; 217; 933; 220; 926; 221; 376; 918; 225; 226; 180; 230;
            224; 8501; 945; 38; 8743; 8736; 39; 229; 8776; 227; 228; 8222; 946; 166; 8226; 8745; 231; 184; 162; 967;
            710; 9827; 8773; 169; 8629; 8746; 164; 8659; 8224; 8595; 176; 948; 9830; 247; 233; 234; 232; 8709; 8195;
            8194; 949; 8801; 951; 240; 235; 8364; 8707; 402; 8704; 189; 188; 190; 8260; 947; 8805; 62; 8660; 8596;
            9829; 8230; 237; 238; 161; 236; 8465; 8734; 8747; 953; 191; 8712; 239; 954; 8656; 955; 9001; 171; 8592;
            8968; 8220; 8804; 8970; 8727; 9674; 8206; 8249; 8216; 60; 175; 8212; 181; 183; 8722; 956; 8711; 160; 8211;
            8800; 8715; 172; 8713; 8836; 241; 957; 243; 244; 339; 242; 8254; 969; 959; 8853; 8744; 170; 186; 248; 245;
            8855; 246; 182; 8706; 8240; 8869; 966; 960; 982; 177; 163; 8242; 8719; 8733; 968; 34; 8658; 8730; 9002;
            187; 8594; 8969; 8221; 8476; 174; 8971; 961; 8207; 8250; 8217; 8218; 353; 8901; 167; 173; 963; 962; 8764;
            9824; 8834; 8838; 8721; 8835; 185; 178; 179; 8839; 223; 964; 8756; 952; 977; 8201; 254; 732; 215; 8482;
            8657; 250; 8593; 251; 249; 168; 978; 965; 252; 8472; 958; 253; 165; 255; 950; 8205; 8204;
        |]
