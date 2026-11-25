namespace CogniCustos.Documents;

/// <summary>An opened document plus the file it was normalised from.</summary>
public sealed record OpenedDocument(DocumentBook Book, string OriginalPath, string? ConvertedFrom)
{
    public string Title => Book.Title;
    public int PageCount => Book.PageCount;
}

/// <summary>
/// The one entry point the app uses: hand it any supported file, get pages back.
///
/// PDF is native. ppt/pptx/doc/docx are converted to PDF once (cached beside the app's data)
/// because one rendering path for everything is worth more than four of them, and because
/// the text layer then comes from the same place regardless of the original format.
/// </summary>
public static class DocumentOpener
{
    public static readonly string[] SupportedExtensions = [".pdf", ".doc", ".docx", ".ppt", ".pptx"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Open a file. Returns the document, or null with a message explaining why not.
    /// <paramref name="cacheDirectory"/> is where converted PDFs live (the app's data dir).
    /// </summary>
    public static OpenedDocument? TryOpen(string path, string cacheDirectory, out string message,
        Action<string>? log = null)
    {
        message = string.Empty;
        if (!File.Exists(path))
        {
            message = "文件不存在：" + path;
            return null;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf")
        {
            try
            {
                return new OpenedDocument(DocumentBook.OpenPdf(path), path, null);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return null;
            }
        }

        if (!OfficePdfConverter.IsOfficeDocument(path))
        {
            message = $"暂不支持 {ext} 文件（支持 PDF / Word / PowerPoint）。";
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            message = "只有 Windows 上才能自动转换 Office 文件；请另存为 PDF 再打开。";
            return null;
        }

        var pdfPath = Path.Combine(cacheDirectory, "reader",
            Path.GetFileNameWithoutExtension(path) + "-" + Fingerprint(path) + ".pdf");

        if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
        {
            if (OfficePdfConverter.TryConvert(path, pdfPath, TimeSpan.FromSeconds(25), log) is { } failure)
            {
                message = failure;
                return null;
            }
        }

        try
        {
            return new OpenedDocument(DocumentBook.OpenPdf(pdfPath), path, pdfPath);
        }
        catch (Exception ex)
        {
            message = "转换后的 PDF 打不开：" + ex.Message;
            return null;
        }
    }

    /// <summary>Reuse a conversion until the source file itself changes.</summary>
    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        var stamp = $"{info.Length}-{info.LastWriteTimeUtc.Ticks}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stamp));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
