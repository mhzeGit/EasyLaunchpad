namespace Launchpad.Core.Search;

public enum FileCategory
{
    Other,
    Image,
    Video,
    Audio,
    Document,
    Code,
    Archive,
    Application,
}

public static class FileTypes
{
    private static readonly Dictionary<FileCategory, string[]> Map = new()
    {
        [FileCategory.Image] = Split("jpg jpeg jpe jfif png gif bmp webp svg ico tif tiff heic heif avif raw cr2 cr3 nef arw dng psd ai xcf"),
        [FileCategory.Video] = Split("mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts 3gp vob ogv"),
        [FileCategory.Audio] = Split("mp3 wav flac aac ogg oga m4a wma opus aiff aif mid midi amr"),
        [FileCategory.Document] = Split("pdf doc docx dot dotx xls xlsx xlsm csv ppt pptx pps ppsx txt rtf odt ods odp md markdown epub mobi azw3 tex one xps pages numbers key log"),
        [FileCategory.Code] = Split("cs csx vb fs js mjs cjs jsx ts tsx py pyw ipynb java kt kts scala c h cpp cc cxx hpp hh rs go rb php pl swift lua dart vue svelte html htm css scss sass less json jsonc xml xaml yaml yml toml ini cfg conf sh bash zsh ps1 psm1 bat cmd sql gradle cmake sln csproj vcxproj props targets"),
        [FileCategory.Archive] = Split("zip rar 7z tar gz tgz bz2 xz zst lz iso img cab wim dmg jar war"),
        [FileCategory.Application] = Split("exe msi msix appx appxbundle msixbundle lnk com scr cpl"),
    };

    private static readonly Dictionary<string, FileCategory> Reverse = BuildReverse();

    private static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, FileCategory> BuildReverse()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        foreach (var (cat, exts) in Map)
            foreach (string e in exts)
                d.TryAdd(e, cat);   // first category wins for ambiguous extensions
        return d;
    }

    public static FileCategory Categorize(string ext) =>
        Reverse.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static IReadOnlyList<string> Extensions(FileCategory c) =>
        Map.TryGetValue(c, out var e) ? e : Array.Empty<string>();

    public static bool TryParseCategory(string word, out FileCategory category)
    {
        switch (word.ToLowerInvariant())
        {
            case "image": case "images": case "picture": case "pictures": case "photo": case "photos": case "img":
                category = FileCategory.Image; return true;
            case "video": case "videos": case "movie": case "movies":
                category = FileCategory.Video; return true;
            case "audio": case "music": case "sound": case "sounds": case "song": case "songs":
                category = FileCategory.Audio; return true;
            case "doc": case "docs": case "document": case "documents": case "text":
                category = FileCategory.Document; return true;
            case "code": case "source": case "src": case "script": case "scripts":
                category = FileCategory.Code; return true;
            case "archive": case "archives": case "zip": case "compressed":
                category = FileCategory.Archive; return true;
            case "app": case "apps": case "application": case "applications": case "program": case "programs": case "exe":
                category = FileCategory.Application; return true;
        }
        category = FileCategory.Other;
        return false;
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Image => "Images",
        FileCategory.Video => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Document => "Documents",
        FileCategory.Code => "Code",
        FileCategory.Archive => "Archives",
        FileCategory.Application => "Programs",
        _ => "Other",
    };
}
