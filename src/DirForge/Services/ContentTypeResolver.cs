using Microsoft.AspNetCore.StaticFiles;

namespace DirForge.Services;

/// <summary>
/// Wraps FileExtensionContentTypeProvider defaults and extends them with
/// additional modern/common mappings for homelab/NAS content.
/// </summary>
public sealed class ContentTypeResolver
{
    private const string DefaultContentType = "application/octet-stream";

    private readonly FileExtensionContentTypeProvider _provider;

    public int DefaultMappingCount { get; }
    public int ExpandedMappingCount => _provider.Mappings.Count;

    public ContentTypeResolver()
    {
        _provider = new FileExtensionContentTypeProvider();
        DefaultMappingCount = _provider.Mappings.Count;

        var duplicateExtensions = AdditionalMappings
            .Select(m => NormalizeExtension(m.Extension))
            .GroupBy(e => e, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToArray();

        if (duplicateExtensions.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate content type mapping extensions detected: {string.Join(", ", duplicateExtensions)}");
        }

        foreach (var (extension, contentType) in AdditionalMappings)
        {
            _provider.Mappings[NormalizeExtension(extension)] = contentType;
        }
    }

    public bool TryGetContentType(string fileName, out string contentType)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            contentType = DefaultContentType;
            return false;
        }

        var found = _provider.TryGetContentType(fileName, out var resolvedContentType);
        contentType = resolvedContentType ?? DefaultContentType;
        return found;
    }

    public string GetContentType(string fileName)
    {
        return TryGetContentType(fileName, out var contentType)
            ? contentType
            : DefaultContentType;
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Content type mapping extension cannot be empty.");
        }

        return trimmed.StartsWith('.')
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }

    private static readonly (string Extension, string ContentType)[] AdditionalMappings =
    [
        // Markup, docs and plain-text variants
        (".md", "text/markdown"),
        (".markdown", "text/markdown"),
        (".mdx", "text/markdown"),
        (".rst", "text/x-rst"),
        (".adoc", "text/asciidoc"),
        (".asciidoc", "text/asciidoc"),
        (".text", "text/plain"),
        (".log", "text/plain"),
        (".conf", "text/plain"),
        (".config", "text/plain"),
        (".ini", "text/plain"),
        (".env", "text/plain"),
        (".toml", "application/toml"),
        (".properties", "text/plain"),
        (".editorconfig", "text/plain"),
        (".gitignore", "text/plain"),
        (".gitattributes", "text/plain"),
        (".gitmodules", "text/plain"),
        (".csv", "text/csv"),
        (".tsv", "text/tab-separated-values"),
        (".yaml", "application/yaml"),
        (".yml", "application/yaml"),
        (".jsonc", "application/json"),
        (".json5", "application/json"),
        (".ndjson", "application/x-ndjson"),
        (".geojson", "application/geo+json"),
        (".webmanifest", "application/manifest+json"),
        (".map", "application/json"),
        (".har", "application/json"),
        (".xhtml", "application/xhtml+xml"),
        (".xslt", "application/xslt+xml"),
        (".xsd", "application/xml"),
        (".xsl", "application/xml"),
        (".svgz", "image/svg+xml"),
        (".ics", "text/calendar"),
        (".vcf", "text/vcard"),
        (".vtt", "text/vtt"),
        (".srt", "application/x-subrip"),
        (".ass", "text/plain"),
        (".ssa", "text/plain"),
        (".nfo", "text/plain"),
        (".sfv", "text/plain"),
        (".md5", "text/plain"), (".md5sum", "text/plain"),
        (".sha1", "text/plain"), (".sha1sum", "text/plain"),
        (".sha256", "text/plain"), (".sha256sum", "text/plain"),
        (".sha512", "text/plain"), (".sha512sum", "text/plain"),
        (".nzb", "application/x-nzb"),

        // Web/dev frontend
        (".wasm", "application/wasm"),
        (".mjs", "text/javascript"),
        (".cjs", "text/javascript"),
        (".jsx", "text/jsx"),
        (".tsx", "text/tsx"),
        (".vue", "text/x-vue"),
        (".svelte", "text/plain"),
        (".astro", "text/plain"),
        (".graphql", "application/graphql"),
        (".gql", "application/graphql"),
        (".proto", "text/plain"),
        (".avif", "image/avif"),
        (".heic", "image/heic"),
        (".heif", "image/heif"),
        (".jxl", "image/jxl"),
        (".icns", "image/icns"),
        (".psd", "image/vnd.adobe.photoshop"),

        // Audio
        (".flac", "audio/flac"),
        (".m4a", "audio/mp4"),
        (".m4b", "audio/mp4"),
        (".m4p", "audio/mp4"),
        (".oga", "audio/ogg"),
        (".ogg", "audio/ogg"),
        (".opus", "audio/opus"),
        (".weba", "audio/webm"),
        (".aac", "audio/aac"),
        (".aif", "audio/aiff"),
        (".aiff", "audio/aiff"),
        (".mid", "audio/midi"),
        (".midi", "audio/midi"),
        (".amr", "audio/amr"),
        (".ac3", "audio/ac3"),
        (".eac3", "audio/eac3"),
        (".dts", "audio/vnd.dts"),
        (".dtshd", "audio/vnd.dts.hd"),
        (".m3u", "audio/x-mpegurl"),
        (".m3u8", "application/vnd.apple.mpegurl"),
        (".pls", "audio/x-scpls"),
        (".ape", "audio/ape"),
        (".mka", "audio/x-matroska"),
        (".wv", "audio/wavpack"),

        // Video
        (".mkv", "video/x-matroska"),
        (".mk3d", "video/x-matroska"),
        (".mks", "video/x-matroska"),
        (".mp4", "video/mp4"),
        (".m4v", "video/mp4"),
        (".mov", "video/quicktime"),
        (".qt", "video/quicktime"),
        (".webm", "video/webm"),
        (".ogv", "video/ogg"),
        (".ts", "video/mp2t"),
        (".mts", "video/mp2t"),
        (".m2ts", "video/mp2t"),
        (".avi", "video/x-msvideo"),
        (".flv", "video/x-flv"),
        (".f4v", "video/mp4"),
        (".m4s", "video/iso.segment"),
        (".vob", "video/dvd"),
        (".mxf", "application/mxf"),
        (".rm", "application/vnd.rn-realmedia"),
        (".rmvb", "application/vnd.rn-realmedia-vbr"),

        // Archives and compression
        (".7z", "application/x-7z-compressed"),
        (".rar", "application/vnd.rar"),
        (".tar", "application/x-tar"),
        (".gz", "application/gzip"),
        (".tgz", "application/gzip"),
        (".bz2", "application/x-bzip2"),
        (".tbz2", "application/x-bzip2"),
        (".xz", "application/x-xz"),
        (".txz", "application/x-xz"),
        (".zst", "application/zstd"),
        (".tzst", "application/zstd"),
        (".lz", "application/x-lzip"),
        (".lzma", "application/x-lzma"),
        (".lz4", "application/x-lz4"),
        (".z", "application/x-compress"),
        (".cab", "application/vnd.ms-cab-compressed"),
        (".ar", "application/x-archive"),
        (".cpio", "application/x-cpio"),
        (".deb", "application/vnd.debian.binary-package"),
        (".rpm", "application/x-rpm"),
        (".torrent", "application/x-bittorrent"),
        (".cbz", "application/vnd.comicbook+zip"),
        (".cbr", "application/vnd.comicbook-rar"),
        (".cb7", "application/x-cb7"),

        // Installer / package / disk images
        (".msi", "application/x-msi"),
        (".msix", "application/msix"),
        (".msixbundle", "application/msixbundle"),
        (".appx", "application/appx"),
        (".appxbundle", "application/appxbundle"),
        (".iso", "application/x-iso9660-image"),
        (".img", "application/octet-stream"),
        (".dmg", "application/x-apple-diskimage"),
        (".apk", "application/vnd.android.package-archive"),
        (".aab", "application/octet-stream"),
        (".ipa", "application/octet-stream"),
        (".nupkg", "application/zip"),
        (".snupkg", "application/zip"),

        // Office and document formats
        (".epub", "application/epub+zip"),
        (".mobi", "application/x-mobipocket-ebook"),
        (".azw", "application/vnd.amazon.ebook"),
        (".azw3", "application/vnd.amazon.ebook"),
        (".docm", "application/vnd.ms-word.document.macroenabled.12"),
        (".dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template"),
        (".dotm", "application/vnd.ms-word.template.macroenabled.12"),
        (".xlsm", "application/vnd.ms-excel.sheet.macroenabled.12"),
        (".xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template"),
        (".xltm", "application/vnd.ms-excel.template.macroenabled.12"),
        (".pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12"),
        (".potx", "application/vnd.openxmlformats-officedocument.presentationml.template"),
        (".potm", "application/vnd.ms-powerpoint.template.macroenabled.12"),
        (".ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow"),
        (".ppsm", "application/vnd.ms-powerpoint.slideshow.macroenabled.12"),
        (".odt", "application/vnd.oasis.opendocument.text"),
        (".ods", "application/vnd.oasis.opendocument.spreadsheet"),
        (".odp", "application/vnd.oasis.opendocument.presentation"),
        (".odg", "application/vnd.oasis.opendocument.graphics"),
        (".odf", "application/vnd.oasis.opendocument.formula"),
        (".latex", "application/x-latex"),
        (".tex", "application/x-tex"),
        (".bib", "text/x-bibtex"),

        // Programming languages
        (".cs", "text/x-csharp"),
        (".fs", "text/x-fsharp"),
        (".fsi", "text/x-fsharp"),
        (".fsx", "text/x-fsharp"),
        (".vb", "text/x-vb"),
        (".sql", "application/sql"),
        (".ps1", "text/plain"),
        (".psm1", "text/plain"),
        (".psd1", "text/plain"),
        (".sh", "application/x-sh"),
        (".bash", "application/x-sh"),
        (".zsh", "application/x-sh"),
        (".fish", "application/x-sh"),
        (".bat", "application/x-msdos-program"),
        (".cmd", "application/x-msdos-program"),
        (".py", "text/x-python"),
        (".pyi", "text/x-python"),
        (".ipynb", "application/x-ipynb+json"),
        (".rb", "text/x-ruby"),
        (".php", "application/x-httpd-php"),
        (".go", "text/x-go"),
        (".rs", "text/x-rust"),
        (".java", "text/x-java-source"),
        (".kt", "text/x-kotlin"),
        (".kts", "text/x-kotlin"),
        (".scala", "text/x-scala"),
        (".sc", "text/x-scala"),
        (".clj", "text/x-clojure"),
        (".cljs", "text/x-clojure"),
        (".groovy", "text/x-groovy"),
        (".gradle", "text/plain"),
        (".swift", "text/x-swift"),
        (".dart", "text/x-dart"),
        (".r", "text/plain"),
        (".jl", "text/plain"),
        (".m", "text/plain"),
        (".mm", "text/plain"),
        (".c", "text/x-c"),
        (".h", "text/x-c"),
        (".cpp", "text/x-c++src"),
        (".cxx", "text/x-c++src"),
        (".cc", "text/x-c++src"),
        (".hpp", "text/x-c++hdr"),
        (".hh", "text/x-c++hdr"),
        (".hxx", "text/x-c++hdr"),
        (".zig", "text/plain"),
        (".nim", "text/plain"),
        (".lua", "text/x-lua"),
        (".pl", "text/x-perl"),
        (".pm", "text/x-perl"),
        (".tcl", "application/x-tcl"),

        // Infra / config / policy
        (".hcl", "application/hcl"),
        (".tf", "application/hcl"),
        (".tfvars", "application/hcl"),
        (".rego", "text/plain"),
        (".cue", "text/plain"),

        // Certificates and keys
        (".pem", "application/x-pem-file"),
        (".key", "application/pgp-keys"),
        (".crt", "application/x-x509-ca-cert"),
        (".cer", "application/pkix-cert"),
        (".der", "application/x-x509-ca-cert"),
        (".p7b", "application/x-pkcs7-certificates"),
        (".p7c", "application/pkcs7-mime"),
        (".p12", "application/x-pkcs12"),
        (".pfx", "application/x-pkcs12"),
        (".jks", "application/octet-stream"),
        (".keystore", "application/octet-stream"),

        // Data and analytics
        (".parquet", "application/vnd.apache.parquet"),
        (".avro", "application/avro"),
        (".orc", "application/octet-stream"),
        (".feather", "application/octet-stream"),
        (".arrow", "application/vnd.apache.arrow.file"),
        (".sqlite", "application/vnd.sqlite3"),
        (".db", "application/octet-stream"),

        // 3D / CAD / GIS
        (".glb", "model/gltf-binary"),
        (".gltf", "model/gltf+json"),
        (".obj", "model/obj"),
        (".mtl", "text/plain"),
        (".stl", "model/stl"),
        (".3mf", "model/3mf"),
        (".fbx", "application/octet-stream"),
        (".dae", "model/vnd.collada+xml"),
        (".blend", "application/octet-stream"),
        (".step", "model/step"),
        (".stp", "model/step"),
        (".iges", "model/iges"),
        (".igs", "model/iges"),
        (".ifc", "application/octet-stream"),
        (".kml", "application/vnd.google-earth.kml+xml"),
        (".kmz", "application/vnd.google-earth.kmz"),
        (".gpx", "application/gpx+xml")
    ];
}
