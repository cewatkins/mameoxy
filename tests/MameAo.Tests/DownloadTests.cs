using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Spludlow.MameAO.Assets;

namespace MameAo.Tests;

public sealed class DownloadTests : IDisposable
{
	private readonly string _tempDirectory;

	public DownloadTests()
	{
		_tempDirectory = Path.Combine(
			Path.GetTempPath(),
			"mameao-download-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDirectory);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
		catch (Exception)
		{
			// Best effort; the OS cleans temp directories.
		}
	}

	[Fact]
	public async Task Download_FreshFile_VerifiesSha1AndMovesIntoPlace()
	{
		byte[] content = CreateContent(256 * 1024 + 17);
		string expectedSha1 = Sha1(content);

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("fresh.bin");

		DownloadResult result = await downloader.DownloadAsync(
			server.BaseUrl + "/fresh.bin",
			destination,
			content.LongLength,
			expectedSha1);

		Assert.Equal(content, await File.ReadAllBytesAsync(destination));
		Assert.False(File.Exists(destination + ".part"));
		Assert.False(File.Exists(destination + ".part.json"));
		Assert.Equal(content.LongLength, result.Bytes);
		Assert.Equal(expectedSha1, result.Sha1);
		Assert.True(result.Verified);
		Assert.Equal(destination, result.Path);
		Assert.Equal(1, server.RequestCount);
	}

	[Fact]
	public async Task Download_TruncatedResponse_ResumesWithRangeAndIfRange()
	{
		byte[] content = CreateContent(512 * 1024);

		using DownloadTestServer server = new DownloadTestServer
		{
			Content = content,
			TruncateFirstResponseAfter = 64 * 1024,
		};
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("resume.bin");

		DownloadResult result = await downloader.DownloadAsync(
			server.BaseUrl + "/resume.bin",
			destination,
			content.LongLength,
			Sha1(content));

		Assert.Equal(content, await File.ReadAllBytesAsync(destination));
		Assert.True(server.RequestCount >= 2);
		Assert.False(File.Exists(destination + ".part"));
		Assert.False(File.Exists(destination + ".part.json"));
		Assert.True(result.Verified);

		RecordedRequest[] requests = server.Requests.ToArray();
		Assert.True(string.IsNullOrEmpty(requests[0].Range));

		RecordedRequest resumed = requests[1];
		Assert.StartsWith("bytes=", resumed.Range, StringComparison.Ordinal);
		Assert.Equal(server.ETag, resumed.IfRange);
	}

	[Fact]
	public async Task Download_ServerError_IsRetriedThenSucceeds()
	{
		byte[] content = CreateContent(4096);

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		server.EnqueueStatus(503, retryAfterSeconds: 0);

		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("retry.bin");

		DownloadResult result = await downloader.DownloadAsync(
			server.BaseUrl + "/retry.bin",
			destination,
			content.LongLength,
			Sha1(content));

		Assert.Equal(content, await File.ReadAllBytesAsync(destination));
		Assert.Equal(2, server.RequestCount);
		Assert.True(result.Verified);
	}

	[Fact]
	public async Task Download_NotFound_IsNotRetried()
	{
		using DownloadTestServer server = new DownloadTestServer { Content = CreateContent(1024) };
		server.EnqueueStatus(404);

		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("missing.bin");

		HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
			downloader.DownloadAsync(
				server.BaseUrl + "/missing.bin?token=topsecret",
				destination,
				expectedSize: 1024,
				expectedSha1: Sha1(CreateContent(1024))));

		Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode!.Value);
		Assert.Equal(1, server.RequestCount);
		Assert.DoesNotContain("topsecret", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
	}

	[Fact]
	public async Task Download_Sha1Mismatch_ThrowsAndRemovesPartialFiles()
	{
		byte[] content = CreateContent(64 * 1024);
		string wrongSha1 = Sha1(CreateContent(64 * 1024 + 1));

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("corrupt.bin");

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
			downloader.DownloadAsync(
				server.BaseUrl + "/corrupt.bin?token=topsecret",
				destination,
				content.LongLength,
				wrongSha1));

		Assert.Contains(wrongSha1, exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(Sha1(content), exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("topsecret", exception.Message, StringComparison.Ordinal);

		Assert.False(File.Exists(destination));
		Assert.False(File.Exists(destination + ".part"));
		Assert.False(File.Exists(destination + ".part.json"));
	}

	[Fact]
	public async Task Download_CancelledMidStream_LeavesPartAndNoDestination()
	{
		byte[] content = CreateContent(2 * 1024 * 1024);

		using DownloadTestServer server = new DownloadTestServer
		{
			Content = content,
			ThrottleChunkSize = 16 * 1024,
			ThrottleDelayMilliseconds = 10,
		};
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("cancel.bin");
		string partPath = destination + ".part";

		using CancellationTokenSource cancellation = new CancellationTokenSource();

		Task<DownloadResult> task = downloader.DownloadAsync(
			server.BaseUrl + "/cancel.bin",
			destination,
			content.LongLength,
			Sha1(content),
			cancellationToken: cancellation.Token);

		await WaitUntilAsync(
			() => File.Exists(partPath) && new FileInfo(partPath).Length > 0,
			TimeSpan.FromSeconds(10));

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

		Assert.False(File.Exists(destination));
		Assert.True(File.Exists(partPath));
		Assert.True(new FileInfo(partPath).Length > 0);
		Assert.True(File.Exists(partPath + ".json"));
	}

	[Fact]
	public async Task Download_RangeNotSatisfiable_TreatsPartAsComplete()
	{
		byte[] content = CreateContent(128 * 1024);

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("complete.bin");
		string partPath = destination + ".part";

		await File.WriteAllBytesAsync(partPath, content);

		DownloadResult result = await downloader.DownloadAsync(
			server.BaseUrl + "/complete.bin",
			destination,
			content.LongLength,
			Sha1(content));

		Assert.Equal(content, await File.ReadAllBytesAsync(destination));
		Assert.Equal(1, server.RequestCount);
		Assert.StartsWith("bytes=" + content.LongLength + "-", server.Requests.Single().Range, StringComparison.Ordinal);
		Assert.False(File.Exists(partPath));
		Assert.True(result.Verified);
		Assert.Equal(content.LongLength, result.Bytes);
	}

	[Fact]
	public async Task Download_SizeMismatch_ThrowsAndRemovesPartialFiles()
	{
		byte[] content = CreateContent(4096);

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string destination = Destination("wrong-size.bin");

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
			downloader.DownloadAsync(
				server.BaseUrl + "/wrong-size.bin",
				destination,
				expectedSize: content.LongLength + 10));

		Assert.Contains("Size mismatch", exception.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
		Assert.False(File.Exists(destination + ".part"));
		Assert.False(File.Exists(destination + ".part.json"));
	}

	[Fact]
	public async Task Download_ManyDownloads_PreservesOrderAndCompletes()
	{
		byte[] content = CreateContent(32 * 1024);
		string sha1 = Sha1(content);

		using DownloadTestServer server = new DownloadTestServer { Content = content };
		using HttpClient http = new HttpClient();
		FileDownloader downloader = new FileDownloader(http, _tempDirectory);

		string[] destinations =
		{
			Destination("many-1.bin"),
			Destination("many-2.bin"),
			Destination("many-3.bin"),
		};

		DownloadJob[] jobs = destinations
			.Select((destination, index) => new DownloadJob(
				server.BaseUrl + "/many-" + index + ".bin",
				destination,
				content.LongLength,
				sha1))
			.ToArray();

		DownloadResult[] results = await downloader.DownloadManyAsync(jobs, maxConcurrency: 2);

		Assert.Equal(destinations, results.Select(result => result.Path).ToArray());
		Assert.All(results, result => Assert.True(result.Verified));

		foreach (string destination in destinations)
			Assert.Equal(content, await File.ReadAllBytesAsync(destination));
	}

	private string Destination(string name)
	{
		return Path.Combine(_tempDirectory, name);
	}

	private static byte[] CreateContent(int length)
	{
		byte[] content = new byte[length];
		for (int index = 0; index < content.Length; ++index)
			content[index] = (byte)((index * 31 + 7) & 0xFF);
		return content;
	}

	private static string Sha1(byte[] content)
	{
		return Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (condition() == false)
		{
			if (stopwatch.Elapsed > timeout)
				throw new TimeoutException("Condition was not met before the timeout.");

			await Task.Delay(10);
		}
	}
}
