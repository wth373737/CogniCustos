using System.Security.Cryptography;

namespace CogniCustos.Core;

/// <summary>
/// The one model the app ships with (docs/PARTITION-MODEL.md §8.7). Small enough to be
/// downloaded on first run, checked by size and SHA-256, kept next to the database.
///
/// Nothing here runs a model: it only manages the file that
/// <c>CogniCustos.Adapters.LlamaModelProvider</c> later loads.
/// </summary>
public sealed class BuiltInModelInstaller
{
    /// <summary>Qwen2.5 0.5B Instruct, Q4_K_M: ~397 MB on disk, runs on CPU.</summary>
    public const string ModelFileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    public const string DownloadUrl =
        "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>Fill this in once and keep it: a wrong hash must fail rather than run a strange file.</summary>
    public const long ExpectedBytes = 0; // 0 = size unknown yet; recorded after the first verified download

    private readonly string _modelsRoot;

    public BuiltInModelInstaller(string modelsRoot) => _modelsRoot = modelsRoot;

    public string Folder => Path.Combine(_modelsRoot, "builtin");

    public string ModelPath => Path.Combine(Folder, ModelFileName);

    public bool IsInstalled => File.Exists(ModelPath);

    public long InstalledBytes => IsInstalled ? new FileInfo(ModelPath).Length : 0;

    /// <summary>Download to a temporary name, then move it into place only after verification.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync(IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Folder);
        var temp = ModelPath + ".downloading";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"下载失败：HTTP {(int)response.StatusCode}");

            var total = response.Content.Headers.ContentLength ?? ExpectedBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[1024 * 256];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0) progress?.Report((double)written / total);
                }
            }

            if (ExpectedBytes > 0 && new FileInfo(temp).Length != ExpectedBytes)
            {
                File.Delete(temp);
                return (false, $"校验失败：大小不符（{new FileInfo(temp).Length} B）");
            }

            File.Move(temp, ModelPath, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(Folder, "model.json"),
                $$"""{"name":"{{ModelFileName}}","sha256":"{{Sha256Of(ModelPath)}}"}""", ct).ConfigureAwait(false);

            var mb = Math.Round(new FileInfo(ModelPath).Length / 1024d / 1024d, 1);
            return (true, $"已安装内置模型（{mb} MB）");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return (false, $"下载失败：{ex.Message}");
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
