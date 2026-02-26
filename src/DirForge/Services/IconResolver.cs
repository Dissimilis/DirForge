namespace DirForge.Services;

public sealed class IconResolver
{
    private static readonly Dictionary<string, string[]> TypeMap = new(StringComparer.Ordinal)
    {
        ["info"] = ["nfo", "diz", "chm", "hlp", "readme.txt", "md", "markdown", "rst"],
        ["document"] = ["acrobat", "pdf", "djvu", "tex", "postscript", "epub", "mobi", "fb2", "ibooks", "lit", "kf8", "azw", "azw3"],
        ["text"] = ["txt", "ini", "inf", "log", "json", "yaml", "yml", "toml", "cfg", "conf", "env", "lock"],
        ["cd"] = ["cue", "img", "iso", "nrg", "daa", "ccd", "cdi", "isz", "bwt", "mds", "mdf", "dmg", "vdi", "vmdk", "vhd", "vhdx", "ova", "ovf", "vcd", "qcow2"],
        ["archive"] = ["rar", "zip", "zoo", "r01", "r02", "r03", "7z", "cab", "ace", "lzw", "arj", "lzh", "jar", "tar", "gz", "bz2", "xz", "lz", "lzma", "zst", "deb", "rpm", "pkg", "tgz", "war", "z", "xpi", "br", "sz", "lz4"],
        ["binary"] = ["bin", "dat", "o", "so", "a", "dll", "lib", "sav", "dump", "hex", "img", "rom", "sys", "class"],
        ["encrypted"] = ["gpg", "pgp", "key", "asc", "enc", "p12", "pfx", "kdbx", "kdb", "pem", "crt", "cer", "der", "csr", "age"],
        ["vectorgfx"] = ["ai", "eps", "svg", "dxf", "dwg", "odg", "cgm", "vsd", "vsdx", "cad"],
        ["image"] = ["png", "jpg", "jpeg", "tiff", "psd", "bmp", "ico", "gif", "jfif", "jp2", "xcf", "heic", "webp", "tif", "tga", "pcx", "ppm", "pgm", "pbm", "dds", "cur", "xbm", "avif", "raw", "cr2", "nef", "dng", "bpg", "cr3", "arw", "orf", "rw2", "jxl"],
        ["font"] = ["ttf", "otf", "woff", "woff2", "eot"],
        ["source"] = ["cgi", "htaccess", "js", "jsx", "tsx", "c", "cpp", "cc", "cxx", "c++", "hpp", "h", "java", "cs", "swift", "dart", "kt", "kts", "rb", "m", "mm", "scala", "vue", "go", "rs", "pl", "py", "sh", "bat", "cmd", "ps1", "less", "sass", "scss", "sql", "xml", "xsl", "xslt", "lua", "coffee", "zig", "bash", "zsh", "csh", "ksh", "tcsh", "sol", "hs", "lisp", "el", "nix", "vb", "r", "jl", "ex", "exs", "elm", "clj", "cljs", "erl", "hrl", "fs", "fsx", "ml", "mli", "nim", "cr", "v", "asm", "s", "sed", "awk", "applescript", "groovy", "gradle", "cmake", "makefile", "mk", "xaml", "svelte", "astro", "handlebars", "hbs", "twig", "tf", "hcl"],
        ["php"] = ["php", "php3", "php4", "php5", "phtml", "phps", "phar"],
        ["qt"] = ["qt", "mov"],
        ["real"] = ["rm", "ram", "ra", "rv", "rmvb"],
        ["video"] = ["avi", "mpg", "mpeg", "wmv", "asf", "mov", "m4v", "mp4", "m2ts", "3gp", "ogv", "ogm", "mkv", "flv", "f4v", "webm", "ts", "vob", "dvi", "m2v", "3g2", "swf"],
        ["sound"] = ["aac", "wma", "mp2", "mp3", "aiff", "mid", "midi", "wav", "flac", "opus", "oga", "ogg", "m4a", "mka", "voc", "w64", "pls", "m3u", "au", "aif", "amr", "caf", "m4r", "m3u8", "xspf"],
        ["rtf"] = ["rtf", "doc", "docx", "docm", "dot", "dotx", "sdw", "wpd", "odt", "ott", "docb", "dotm", "pages", "wps"],
        ["spreadsheet"] = ["ods", "xls", "xlsx", "xlsm", "xlsb", "xlw", "dbf", "xlt", "sdc", "csv", "tsv", "numbers", "xlm", "xltm", "xltx"],
        ["pres"] = ["ppt", "pptx", "pptm", "pps", "odp", "otp", "cgm", "key", "ppsx", "potx", "sldm", "sldx"],
        ["program"] = ["exe", "elf", "apk", "msi", "app", "appimage", "ipa", "com", "msu", "snap", "flatpak"],
        ["ht"] = ["htm", "html", "shtml", "xhtml", "css", "jsp", "asp", "aspx", "rss"]
    };

    private static readonly IReadOnlyDictionary<string, string> FullNameTypeMap = CreateFullNameTypeMap();
    private static readonly IReadOnlyDictionary<string, string> ExtensionTypeMap = CreateExtensionTypeMap();

    private readonly HashSet<string> _availableIcons;

    public IconResolver(IWebHostEnvironment hostEnvironment)
    {
        var webRoot = hostEnvironment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var iconsPath = Path.Combine(webRoot, "dirforge-assets", "file-icon-vectors");

        _availableIcons = Directory.Exists(iconsPath)
            ? Directory.EnumerateFiles(iconsPath, "*.svg", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    public string ResolveType(string fullPath, bool isDirectory, long size)
    {
        if (isDirectory)
        {
            return "folder";
        }

        var fileName = Path.GetFileName(fullPath).ToLowerInvariant();
        if (FullNameTypeMap.TryGetValue(fileName, out var fullNameType))
        {
            return fullNameType;
        }

        var extension = GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && ExtensionTypeMap.TryGetValue(extension, out var extensionType))
        {
            return extensionType;
        }

        if (size == 0)
        {
            return "empty";
        }

        return "misc";
    }

    public string ResolveIconPath(string fileName, string type)
    {
        var extension = GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && _availableIcons.Contains(extension + ".svg"))
        {
            return StaticAssetRouteHelper.AssetPath("file-icon-vectors/" + extension + ".svg");
        }

        if (_availableIcons.Contains(type + ".svg"))
        {
            return StaticAssetRouteHelper.AssetPath("file-icon-vectors/" + type + ".svg");
        }

        return StaticAssetRouteHelper.AssetPath("file-icon-vectors/blank.svg");
    }

    public static string GetExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? string.Empty : extension.TrimStart('.').ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> CreateFullNameTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in TypeMap)
        {
            foreach (var suffix in pair.Value)
            {
                if (!suffix.Contains('.'))
                {
                    continue;
                }

                if (!map.ContainsKey(suffix))
                {
                    map[suffix] = pair.Key;
                }
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> CreateExtensionTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in TypeMap)
        {
            foreach (var suffix in pair.Value)
            {
                if (suffix.Contains('.'))
                {
                    continue;
                }

                if (!map.ContainsKey(suffix))
                {
                    map[suffix] = pair.Key;
                }
            }
        }

        return map;
    }
}
