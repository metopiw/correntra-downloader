using System.Collections.Immutable;
using Correntra.Core.Internal;
using Correntra.Core.Security;

namespace Correntra.Core.Categories;

public sealed class DownloadCategory
{
    public DownloadCategory(
        CategoryId id,
        string name,
        string destinationFolderName,
        IEnumerable<string>? fileExtensions = null,
        IEnumerable<string>? contentTypePrefixes = null,
        bool isBuiltIn = false,
        string? localizationKey = null)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A category ID cannot be empty.", nameof(id));
        }

        Id = id;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 64);
        DestinationFolderName = SafePath.ValidateComponent(destinationFolderName, nameof(destinationFolderName), 64);
        FileExtensions = CategoryRule.NormalizeExtensions(fileExtensions, nameof(fileExtensions));
        ContentTypePrefixes = CategoryRule.NormalizeContentTypes(contentTypePrefixes, nameof(contentTypePrefixes));
        IsBuiltIn = isBuiltIn;
        LocalizationKey = localizationKey is null
            ? null
            : Guard.NotNullOrWhiteSpace(localizationKey, nameof(localizationKey), 100);
    }

    public CategoryId Id { get; }

    public string Name { get; }

    public string DestinationFolderName { get; }

    public ImmutableHashSet<string> FileExtensions { get; }

    public ImmutableHashSet<string> ContentTypePrefixes { get; }

    public bool IsBuiltIn { get; }

    public string? LocalizationKey { get; }

    public bool Matches(CategoryMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.FileExtension is { } extension && FileExtensions.Contains(extension)) ||
            (context.ContentType is { } contentType &&
             ContentTypePrefixes.Any(prefix => contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }
}

public static class DefaultDownloadCategories
{
    public static readonly CategoryId CompressedId = new(new Guid("1d6ace6f-6a63-4cb2-b2a1-266723081901"));
    public static readonly CategoryId DocumentsId = new(new Guid("2dd47632-e238-45f1-8616-c31c74a1ac02"));
    public static readonly CategoryId MusicId = new(new Guid("3bc1b346-64e0-44f2-9b22-f9ac2df72703"));
    public static readonly CategoryId ProgramsId = new(new Guid("45d48dd3-cb44-41b8-99d4-49a322f88d04"));
    public static readonly CategoryId VideoId = new(new Guid("5ecf78a0-c436-423a-a915-a706f2329905"));

    public static ImmutableArray<DownloadCategory> All { get; } =
    [
        new(
            CompressedId,
            "Compressed",
            "Compressed",
            [".7z", ".bz2", ".gz", ".iso", ".rar", ".tar", ".tgz", ".xz", ".zip"],
            ["application/gzip", "application/vnd.rar", "application/x-7z-compressed", "application/zip"],
            true,
            "Category.Compressed"),
        new(
            DocumentsId,
            "Documents",
            "Documents",
            [".csv", ".doc", ".docx", ".epub", ".md", ".odt", ".pdf", ".ppt", ".pptx", ".rtf", ".txt", ".xls", ".xlsx"],
            ["application/epub+zip", "application/pdf", "application/rtf", "application/vnd.ms-", "application/vnd.openxmlformats-", "text/"],
            true,
            "Category.Documents"),
        new(
            MusicId,
            "Music",
            "Music",
            [".aac", ".aiff", ".alac", ".flac", ".m4a", ".mp3", ".oga", ".ogg", ".opus", ".wav", ".wma"],
            ["audio/"],
            true,
            "Category.Music"),
        new(
            ProgramsId,
            "Programs",
            "Programs",
            [".appx", ".appxbundle", ".bat", ".cmd", ".com", ".exe", ".msi", ".msix", ".msixbundle"],
            ["application/vnd.microsoft.portable-executable", "application/x-msdownload", "application/x-msi"],
            true,
            "Category.Programs"),
        new(
            VideoId,
            "Video",
            "Video",
            [".3gp", ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ts", ".webm", ".wmv"],
            ["application/vnd.apple.mpegurl", "application/dash+xml", "video/"],
            true,
            "Category.Video"),
    ];
}
