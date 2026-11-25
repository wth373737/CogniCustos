using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CogniCustos.Core;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Sample;

public sealed partial class SampleWindow : Window
{
    private readonly RuntimeShellDataSource _source;

    public SampleWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var root = Environment.GetEnvironmentVariable("COGNICUSTOS_HOME");
        root ??= Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(root);

        var runtime = CogniCustosRuntime.Create(root);
        runtime.CloudModels.SetFactory((url, model, key) =>
            new CogniCustos.Adapters.OpenAiCompatibleProvider(url, model, key));
        runtime.LocalModels.BuiltInFactory = (path, gpu) =>
            new CogniCustos.Adapters.LlamaModelProvider(path, gpuLayers: gpu ? 99 : 0, preferVulkan: gpu);

        // The reader's vision path only matters for boxes with no text layer; if Ollama is
        // not running this stays unavailable and the reader says so instead of guessing.
        var vision = new CogniCustos.Adapters.OllamaVisionModel("http://127.0.0.1:11434", "qwen3-vl:4b");
        _source = new RuntimeShellDataSource(runtime, vision);

        // Demo convenience: when nothing is configured yet, borrow a local Ollama model so the
        // reader can actually explain something without a setup step. Anything the user
        // configures in Settings outranks this, because it is registered last.
        BorrowLocalModel(runtime);

        // today's fetch runs before the shell is built, so freshly proposed items are
        // already on the incoming card when the window opens
        if (runtime.Poller.IsDue(DateTime.Today))
        {
            try
            {
                runtime.Poller.RunAsync(DateTime.Today).GetAwaiter().GetResult();
            }
            catch
            {
                // a failing source must not stop the app from opening
            }
        }

        DataContext = new ShellViewModel(_source);
        Title = $"CogniCustos — Sample · {_source.DataRoot}";
    }

    /// <summary>
    /// Register a local Ollama model as the session's fallback, but only when nothing else is
    /// available and only if one is already installed. This is what makes a fresh demo folder
    /// able to answer questions without a configuration step; it changes no settings on disk.
    /// </summary>
    private static void BorrowLocalModel(CogniCustosRuntime runtime)
    {
        try
        {
            var already = runtime.Models.All.Any(p =>
                p.Role == CogniCustos.Contracts.ModelRole.Reasoning && p.IsAvailable);
            if (already) return;

            const string url = "http://127.0.0.1:11434";
            var probe = new CogniCustos.Adapters.OllamaModelProvider(url, "probe");
            if (!probe.IsAvailable) return;

            var installed = probe.ListModelsAsync().GetAwaiter().GetResult();
            var pick = installed.FirstOrDefault(m => m.StartsWith("qwen2.5:7b", StringComparison.OrdinalIgnoreCase))
                       ?? installed.FirstOrDefault(m => !m.Contains("vl", StringComparison.OrdinalIgnoreCase)
                                                       && !m.Contains("coder", StringComparison.OrdinalIgnoreCase)
                                                       && !m.Contains("r1", StringComparison.OrdinalIgnoreCase))
                       ?? installed.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pick)) return;

            runtime.AddModel(new CogniCustos.Adapters.OllamaModelProvider(url, pick));
        }
        catch
        {
            // a demo without a model still works: the reader falls back to the plain text
        }
    }

    /// <summary>Escape closes the dialog / the calendar / overlays, like the sandbox.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel vm)
        {
            if (vm.Dialog.IsOpen) vm.Dialog.CancelCommand.Execute(null);
            else if (vm.Calendar.IsVisible) vm.Calendar.Close();
            else if (vm.Reader.IsOpen) vm.Reader.Close();
            else if (vm.Advanced.IsOpen) vm.Advanced.Close();
            else vm.CloseOverlays();

            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
