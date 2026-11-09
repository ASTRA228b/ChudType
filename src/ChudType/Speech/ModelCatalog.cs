using System.Net.Http;
using System.Security.Cryptography;

namespace ChudType.Speech;

public sealed record ModelSpec(string Id, string Display, long Bytes, string Sha256)
{
    public string FileName => $"ggml-{Id}.bin";
    public string Size => $"{Bytes / 1_000_000} MB";
    public override string ToString() => $"{Display} · {Size}";
}

public sealed class ModelCatalog
{
    public static IReadOnlyList<ModelSpec> Models { get; } =
    [
        new("tiny", "Whisper Tiny", 77691713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new("base", "Whisper Base", 147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "Whisper Small", 487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b")
    ];

    private readonly string directory;
    private readonly HttpClient http;
    private readonly SemaphoreSlim download = new(1, 1);
    private readonly Dictionary<string, (long Length, DateTime Modified)> verified = new();
    private const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public ModelCatalog(ChudType.Storage.LocalStore store, HttpClient http)
    {
        directory = Path.Combine(store.DirectoryPath, "models");
        Directory.CreateDirectory(directory);
        this.http = http;
    }

    public string FilePath(ModelSpec model) => Path.Combine(directory, model.FileName);
    public static ModelSpec Get(string id) => Models.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException("Select a supported model.");
    public bool IsInstalled(string id)
    {
        var model = Get(id);
        var info = new FileInfo(FilePath(model));
        return info.Exists && info.Length == model.Bytes;
    }

    public async Task<string> OpenVerifiedAsync(string id, CancellationToken cancellation)
    {
        var model = Get(id);
        string path = FilePath(model);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Download the selected Whisper model in Models first.");
        var signature = (info.Length, info.LastWriteTimeUtc);
        lock (verified)
            if (verified.TryGetValue(id, out var prior) && prior == signature) return path;

        await using var source = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(source, cancellation);
        if (info.Length != model.Bytes || !Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Whisper model failed verification. Download it again from Models.");
        lock (verified) verified[id] = signature;
        return path;
    }

    public async Task DownloadAsync(string id, IProgress<double> progress, CancellationToken cancellation)
    {
        var model = Get(id);
        await download.WaitAsync(cancellation);
        string file = FilePath(model);
        string partial = file + ".download";
        try
        {
            string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{model.FileName}?download=true";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count;
                    if (total > model.Bytes) throw new InvalidDataException("The model download exceeded its expected size.");
                    sha.AppendData(buffer.AsSpan(0, count));
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    progress.Report((double)total / model.Bytes);
                }
            }
            if (total != model.Bytes || !Convert.ToHexString(sha.GetHashAndReset()).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded model failed SHA-256 verification.");
            cancellation.ThrowIfCancellationRequested();
            File.Move(partial, file, overwrite: true);
            lock (verified) verified.Remove(id);
        }
        finally
        {
            try { File.Delete(partial); }
            finally { download.Release(); }
        }
    }
}
