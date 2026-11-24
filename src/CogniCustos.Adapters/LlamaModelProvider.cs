using System.Runtime.InteropServices;
using System.Text;
using CogniCustos.Contracts;
using LLama;
using LLama.Common;
using LLama.Native;

namespace CogniCustos.Adapters;

/// <summary>
/// The bundled local model (docs/PARTITION-MODEL.md §8.7): llama.cpp through LLamaSharp,
/// loading a GGUF file from <c>&lt;root&gt;/models/builtin</c>. Loading is lazy so starting
/// the app costs nothing until the model is actually used.
///
/// Honest failure: when the file is missing the provider reports unavailable and the
/// router falls back to standby instead of pretending to answer.
/// </summary>
public sealed class LlamaModelProvider : IModelProvider, IDisposable
{
    private readonly string _modelPath;
    private readonly int _contextSize;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;

    public LlamaModelProvider(string modelPath, int contextSize = 2048)
    {
        _modelPath = modelPath;
        _contextSize = contextSize;
    }

    public string Id => "builtin:" + Path.GetFileNameWithoutExtension(_modelPath);

    public ModelRole Role => ModelRole.Reasoning;

    public bool IsAvailable => File.Exists(_modelPath);

    /// <summary>Qwen2.5 / Qwen2 template; a single place to change if the bundled model changes.</summary>
    private static string Wrap(string system, string user) =>
        $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        if (!IsAvailable) return "(内置模型文件不存在)";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var executor = EnsureExecutor();
            var parameters = new InferenceParams
            {
                MaxTokens = 512,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
            };

            var builder = new StringBuilder();
            await foreach (var token in executor.InferAsync(Wrap(system, user), parameters, ct).ConfigureAwait(false))
            {
                builder.Append(token);
            }

            return builder.ToString().Trim();
        }
        catch (Exception ex)
        {
            var chain = new System.Text.StringBuilder();
            for (var current = ex; current is not null; current = current.InnerException)
            {
                if (chain.Length > 0) chain.Append(" <- ");
                chain.Append($"{current.GetType().Name}: {current.Message}");
            }

            return $"(内置模型调用失败：{chain})";
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    /// <summary>
    /// The CPU backend ships its libraries under <c>runtimes/&lt;rid&gt;/native</c> (best build
    /// in avx2/). The default loader only looks beside the executable, so put the best
    /// build there once. No API surface needed: one copy, then the normal probing works.
    /// </summary>
    /// <summary>
    /// The CPU backend ships several native libraries (llama.dll plus ggml*, mtmd) under
    /// <c>runtimes/&lt;rid&gt;/native</c>, with the best build in avx2/. The default loader
    /// only probes beside the executable, so copy that whole set there once.
    /// </summary>
    private static void EnsureNativeBesideApp()
    {
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.RuntimeIdentifier, "native");
        if (!Directory.Exists(native)) return;

        var best = Path.Combine(native, "avx2");
        var source = Directory.Exists(best) ? best : native;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(file));
            if (File.Exists(target)) continue;

            try
            {
                File.Copy(file, target, overwrite: false);
            }
            catch (IOException)
            {
                // another process copied it first; the loader will find it either way
            }
        }
    }

    /// <summary>
    /// llama.cpp opens the GGUF with a narrow (non-Unicode) file call on Windows, so a data
    /// directory whose path contains non-ASCII characters cannot be opened by it. When that
    /// happens the file is mirrored once into an ASCII path under LocalApplicationData and
    /// loaded from there; everything else keeps using the real location.
    /// </summary>
    private string ResolveLoadPath()
    {
        if (_modelPath.All(c => c < 128)) return _modelPath;

        // a Windows 8.3 short path is plain ASCII and points at the same file, so a
        // non-ASCII data directory works without copying half a gigabyte
        var shortPath = ShortPathOf(_modelPath);
        if (!string.IsNullOrEmpty(shortPath) && shortPath.All(c => c < 128)) return shortPath;

        var asciiDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CogniCustos", "models");
        Directory.CreateDirectory(asciiDir);

        var mirrored = Path.Combine(asciiDir, Path.GetFileName(_modelPath));
        var sourceLength = new FileInfo(_modelPath).Length;
        if (File.Exists(mirrored) && new FileInfo(mirrored).Length == sourceLength) return mirrored;

        File.Copy(_modelPath, mirrored, overwrite: true);
        return mirrored;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    private static string? ShortPathOf(string path)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private StatelessExecutor EnsureExecutor()
    {
        if (_executor is not null) return _executor;

        // the CPU backend ships its native library under runtimes/<rid>/native (with AVX
        // variants in subfolders); point the loader there before the first native call
        EnsureNativeBesideApp();

        var parameters = new ModelParams(ResolveLoadPath())
        {
            ContextSize = (uint)_contextSize,
            GpuLayerCount = 0,
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _executor = new StatelessExecutor(_weights, parameters);
        return _executor;
    }

    public void Dispose()
    {
        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _gate.Dispose();
    }
}
