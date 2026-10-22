using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Moreno.Voice.Services;

// One downloadable model: a Hugging Face repository pinned to an exact commit, with the size and SHA-256 of every file so a changed or tampered upstream file is rejected.
internal sealed record ModelSpec(string Name, string Repository, string Revision, ModelFile[] Files)
{
	public string BaseUrl => $"https://huggingface.co/{Repository}/resolve/{Revision}/";
}

internal sealed record ModelFile(string Name, long Bytes, string? Sha256);

internal static class ModelInstaller
{
	public static readonly ModelSpec Streaming = new(
		"sherpa-onnx-streaming-zipformer-en-2023-06-21",
		"csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-21",
		"9a65b6ea94c311ca770c2bf895b30f456a22d703",
		[
			new("tokens.txt", 5048, null),
			new("encoder-epoch-99-avg-1.int8.onnx", 187823992, "32c98281c7bd8b63e3e142d007251b37f120572e8fdea9a4f5a79ce22b10ec4f"),
			new("decoder-epoch-99-avg-1.onnx", 2092566, "9da02b77cb08826756ec6a88635f35a40374e4164e7c6359121a9145958a6ceb"),
			new("joiner-epoch-99-avg-1.int8.onnx", 259335, "831477d390e59a61f1b6a6f763b9903e6c6366ff6034f1ddba613be82637122f"),
		]);

	// NVIDIA Parakeet TDT 0.6B v2 (CC-BY-4.0), used to re-read finished sentences with punctuation and capitalization.
	public static readonly ModelSpec Accuracy = new(
		"sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
		"csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
		"1ab9323565ddb038682214b292f588070a538ce2",
		[
			new("tokens.txt", 9384, null),
			new("decoder.int8.onnx", 7257753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
			new("joiner.int8.onnx", 1739080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
			new("encoder.int8.onnx", 652184296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
		]);

	private static readonly string ModelRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moreno.Voice", "Models");

	public static string DirectoryOf(ModelSpec spec) => Path.Combine(ModelRoot, spec.Name);

	public static bool IsInstalled(ModelSpec spec) => spec.Files.All(file => HasFile(spec, file));

	public static async Task<string> EnsureInstalledAsync(ModelSpec spec, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
	{
		var directory = DirectoryOf(spec);
		if (IsInstalled(spec)) return directory;
		Directory.CreateDirectory(directory);
		using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Moreno.Voice/0.1");
		var totalBytes = spec.Files.Sum(file => file.Bytes);
		long finishedBytes = 0;
		foreach (var file in spec.Files)
		{
			if (HasFile(spec, file))
			{
				finishedBytes += file.Bytes;
				progress?.Report((double)finishedBytes / totalBytes);
				continue;
			}
			var outputPath = Path.Combine(directory, file.Name);
			var downloadPath = $"{outputPath}.download";
			try
			{
				await DownloadAsync(client, spec.BaseUrl + file.Name, downloadPath, file, read => progress?.Report((double)(finishedBytes + read) / totalBytes), cancellationToken).ConfigureAwait(false);
				File.Move(downloadPath, outputPath, true);
				finishedBytes += file.Bytes;
				progress?.Report((double)finishedBytes / totalBytes);
			}
			finally
			{
				if (File.Exists(downloadPath)) File.Delete(downloadPath);
			}
		}
		if (!IsInstalled(spec)) throw new InvalidDataException("The downloaded speech model is missing required files.");
		return directory;
	}

	private static async Task DownloadAsync(HttpClient client, string url, string path, ModelFile file, Action<long> reportRead, CancellationToken cancellationToken)
	{
		using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		long total = 0;
		await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
		await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
		{
			var buffer = new byte[131072];
			int read;
			while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
			{
				total += read;
				hash.AppendData(buffer, 0, read);
				await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				reportRead(total);
			}
		}
		if (total != file.Bytes) throw new InvalidDataException($"{file.Name} has the wrong size ({total} bytes, expected {file.Bytes}).");
		if (file.Sha256 is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{file.Name} failed its integrity check.");
	}

	// Installed files are only checked by size at startup; hashing hundreds of megabytes on every launch is not worth it, and the hash is verified when the file is downloaded.
	private static bool HasFile(ModelSpec spec, ModelFile file)
	{
		var info = new FileInfo(Path.Combine(DirectoryOf(spec), file.Name));
		return info.Exists && info.Length == file.Bytes;
	}
}
