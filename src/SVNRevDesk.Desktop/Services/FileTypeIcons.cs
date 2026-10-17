using System.IO;

namespace SVNRevDesk.Desktop.Services;

public enum FileTypeKind
{
    Folder,
    Image,
    Text,
    Archive,
    Word,
    Excel,
    PowerPoint,
    Pdf,
    Code,
    Configuration,
    Audio,
    Video,
    Application,
    Database,
    Font,
    Unknown
}

// Resolve from the name only, so server files have icons even before they are downloaded.
public static class FileTypeIcons
{
    public static FileTypeKind GetKind(string name, bool isDirectory)
    {
        if (isDirectory) return FileTypeKind.Folder;

        var fileName = Path.GetFileName(name).ToLowerInvariant();
        if (fileName is "readme" or "license" or "licence" or "changelog" or "authors")
            return FileTypeKind.Text;
        if (fileName is ".gitignore" or ".gitattributes" or ".editorconfig" or ".svnignore" or ".env")
            return FileTypeKind.Configuration;
        if (fileName is "dockerfile" or "makefile" or "cmakelists.txt")
            return FileTypeKind.Code;

        return Path.GetExtension(fileName) switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico"
                or ".tif" or ".tiff" or ".heic" or ".avif" or ".psd" => FileTypeKind.Image,
            ".txt" or ".md" or ".markdown" or ".log" or ".rst" or ".rtf" => FileTypeKind.Text,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz"
                or ".zst" or ".cab" or ".iso" => FileTypeKind.Archive,
            ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".odt" => FileTypeKind.Word,
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".tsv" or ".ods" => FileTypeKind.Excel,
            ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => FileTypeKind.PowerPoint,
            ".pdf" => FileTypeKind.Pdf,
            ".cs" or ".vb" or ".fs" or ".cpp" or ".c" or ".h" or ".hpp" or ".java"
                or ".kt" or ".py" or ".js" or ".jsx" or ".ts" or ".tsx" or ".vue" or ".svelte"
                or ".html" or ".htm" or ".css" or ".scss" or ".less" or ".sql" or ".go" or ".rs"
                or ".php" or ".rb" or ".swift" or ".sh" or ".bat" or ".cmd" or ".ps1" or ".xaml"
                or ".tex" => FileTypeKind.Code,
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf"
                or ".config" or ".props" or ".targets" or ".csproj" or ".vbproj" or ".fsproj"
                or ".sln" or ".slnx" or ".manifest" => FileTypeKind.Configuration,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".wma" or ".mid" => FileTypeKind.Audio,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".mpeg" or ".mpg" or ".m4v" => FileTypeKind.Video,
            ".exe" or ".dll" or ".msi" or ".msix" or ".appx" => FileTypeKind.Application,
            ".db" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".bak" => FileTypeKind.Database,
            ".ttf" or ".otf" or ".woff" or ".woff2" => FileTypeKind.Font,
            _ => FileTypeKind.Unknown
        };
    }

    public static string GetLabel(FileTypeKind kind) => kind switch
    {
        FileTypeKind.Folder => "文件夹",
        FileTypeKind.Image => "图片",
        FileTypeKind.Text => "文本",
        FileTypeKind.Archive => "压缩包 / 镜像",
        FileTypeKind.Word => "Word 文档",
        FileTypeKind.Excel => "表格",
        FileTypeKind.PowerPoint => "演示文稿",
        FileTypeKind.Pdf => "PDF 文档",
        FileTypeKind.Code => "代码",
        FileTypeKind.Configuration => "配置 / 项目文件",
        FileTypeKind.Audio => "音频",
        FileTypeKind.Video => "视频",
        FileTypeKind.Application => "程序 / 组件",
        FileTypeKind.Database => "数据库 / 备份",
        FileTypeKind.Font => "字体",
        _ => "其他文件"
    };
}
