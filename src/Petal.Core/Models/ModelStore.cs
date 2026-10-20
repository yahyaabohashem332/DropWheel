using System.Security.Cryptography;

namespace Petal.Core.Models;

/// <summary>
/// An open AI model Petal runs locally. Bundled models ship in the app's tools\models folder;
/// the others are downloaded once, when a tool first needs them, and checked against
/// <see cref="Sha256"/> before use.
/// </summary>
/// <param name="FileName">The model's file (for a bundled model, relative to tools\models).</param>
public sealed record ModelInfo(string Id, string Name, string FileName, string Url, string Sha256, long Size, string License, bool Bundled = false);

public static class ModelCatalog
{
    /// <summary>BiRefNet lite (MIT): cuts the subject out of a photo, edges and hair included.</summary>
    public static readonly ModelInfo Background = new(
        "birefnet-lite", "BiRefNet lite", "birefnet-lite.onnx",
        "https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx",
        "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", 224_005_088, "MIT");

    /// <summary>Real-ESRGAN general x4v3 (BSD-3-Clause), Qualcomm's ONNX export: 4× enlargement. Ships with Petal.</summary>
    public static readonly ModelInfo Upscaler = new(
        "realesrgan-x4v3", "Real-ESRGAN general x4v3", @"realesrgan-x4v3\real_esrgan_general_x4v3.onnx",
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_general_x4v3/releases/v0.64.0/real_esrgan_general_x4v3-onnx-float.zip",
        "fc094329b24a00c936ebc1cc4c51bc59633e7dc36f4a553344342171cbd3c7cf", 4_540_895, "BSD-3-Clause", Bundled: true);

    /// <summary>Whisper base, 5-bit (MIT): quick, best for clear English.</summary>
    public static readonly ModelInfo WhisperFast = new(
        "whisper-base", "Whisper base", "ggml-base-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin",
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", 59_707_625, "MIT");

    /// <summary>Whisper small, 5-bit (MIT): a good balance for most languages.</summary>
    public static readonly ModelInfo WhisperBalanced = new(
        "whisper-small", "Whisper small", "ggml-small-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487, "MIT");

    /// <summary>Whisper large-v3 turbo, 5-bit (MIT): the most accurate, Arabic included.</summary>
    public static readonly ModelInfo WhisperAccurate = new(
        "whisper-large-v3-turbo", "Whisper large-v3 turbo", "ggml-large-v3-turbo-q5_0.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195, "MIT");

    public static IReadOnlyList<ModelInfo> All { get; } = [Background, Upscaler, WhisperFast, WhisperBalanced, WhisperAccurate];
}

/// <summary>Where models live, and the verified download of the ones that don't ship with Petal.</summary>
public static class ModelStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Downloaded models: %LOCALAPPDATA%\Petal\models (overridable for tests).</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "models");

    /// <summary>Bundled models, next to the app.</summary>
    public static string BundledFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "models");

    /// <summary>The model's file when it is on this PC (and complete), else null.</summary>
    public static string? Find(ModelInfo model)
    {
        if (model.Bundled)
        {
            var bundled = Path.Combine(BundledFolder, model.FileName);
            if (File.Exists(bundled))
                return bundled;
            // Development builds: tools\bin\models at the repository root.
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var dev = Path.Combine(dir.FullName, "tools", "bin", "models", model.FileName);
                if (File.Exists(dev))
                    return dev;
            }
            return null;
        }
        var path = Path.Combine(Folder, model.FileName);
        return File.Exists(path) && new FileInfo(path).Length == model.Size ? path : null;
    }

    public static bool IsAvailable(ModelInfo model) => Find(model) is not null;

    /// <summary>
    /// Downloads the model, checks its SHA-256, and only then moves it into place. A download
    /// that is interrupted or doesn't match leaves nothing behind.
    /// </summary>
    public static async Task<string> DownloadAsync(ModelInfo model, IProgress<double>? progress = null, CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        if (model.Bundled)
            return Find(model) ?? throw new FileNotFoundException($"{model.Name} should come with Petal but is missing. Reinstall Petal to restore it.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Find(model) is { } existing)
                return existing;
            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, model.FileName);
            var part = target + ".part";
            var http = client ?? Shared.Value;
            try
            {
                using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? model.Size;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        done += read;
                        progress?.Report(Math.Min(1, (double)done / Math.Max(1, total)));
                    }
                    var actual = Convert.ToHexString(hash.GetHashAndReset());
                    if (!actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The downloaded {model.Name} didn't match its checksum, so it wasn't used. Try again later.");
                }
                File.Move(part, target, overwrite: true);
                return target;
            }
            catch
            {
                try { File.Delete(part); } catch (IOException) { }
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes a downloaded model to free space (bundled models stay).</summary>
    public static void Delete(ModelInfo model)
    {
        if (!model.Bundled && Find(model) is { } path)
            File.Delete(path);
    }

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Petal/1.0");
        return client;
    });
}
