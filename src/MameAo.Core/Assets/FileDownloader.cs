using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Spludlow.MameAO.Assets
{
	/// <summary>
	/// Outcome of a completed download.
	/// </summary>
	/// <param name="Path">Absolute path of the finished file.</param>
	/// <param name="Bytes">Length of the finished file.</param>
	/// <param name="Sha1">Lower-case hexadecimal SHA1 of the finished file.</param>
	/// <param name="Verified">True when a caller-supplied size or SHA1 check passed.</param>
	public sealed record DownloadResult(string Path, long Bytes, string Sha1, bool Verified);

	/// <summary>
	/// One item for <see cref="FileDownloader.DownloadManyAsync"/>.
	/// </summary>
	public sealed record DownloadJob(
		string Url,
		string Destination,
		long? ExpectedSize = null,
		string ExpectedSha1 = null,
		IReadOnlyDictionary<string, string> Headers = null);

	/// <summary>
	/// Options for <see cref="FileDownloader.Download(string, string, DownloadOptions)"/>
	/// and <see cref="FileDownloader.DownloadAsync(string, string, DownloadOptions, System.Threading.CancellationToken)"/>.
	/// Immutable, safe to share between concurrent downloads.
	/// </summary>
	public sealed class DownloadOptions
	{
		/// <summary>Expected length in bytes. 0 (the default) means unknown.</summary>
		public long ExpectedSize { get; init; }

		/// <summary>Expected hexadecimal SHA1. Null (the default) skips verification.</summary>
		public string ExpectedSha1 { get; init; }

		/// <summary>
		/// Value for the Cookie header, for example archive.org's
		/// "logged-in-user=...; logged-in-sig=...". Null (the default) sends none.
		/// </summary>
		public string AuthCookie { get; init; }

		/// <summary>Number of attempts, including the first. Defaults to 5.</summary>
		public int MaxAttempts { get; init; } = 5;

		/// <summary>
		/// Progress sink. Called with (bytesSoFar, totalOrZero), where 0 means the
		/// total size is not known. Exceptions thrown by the sink are ignored.
		/// </summary>
		public Action<long, long> Progress { get; init; }
	}

	/// <summary>
	/// Resumable, checksum-verified HTTP downloader.
	///
	/// Data is written to "destination.part" with a small "destination.part.json"
	/// sidecar holding the validator and total length. A destination file only
	/// appears (via an atomic rename) after the size and optional SHA1 checks pass,
	/// so an interrupted transfer can never leave a plausible-looking partial file.
	///
	/// The supplied <see cref="HttpClient"/> should have
	/// <see cref="HttpClient.Timeout"/> set to <see cref="Timeout.InfiniteTimeSpan"/>
	/// for very large transfers; this class enforces its own six-hour limit per
	/// attempt with a linked cancellation token.
	/// </summary>
	public sealed class FileDownloader
	{
		private const int MaxAttempts = 5;
		private const int BufferSize = 1024 * 1024;
		private const int FileBufferSize = 4096;
		private const int ProgressIntervalMilliseconds = 250;

		private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan DownloadTimeout = TimeSpan.FromHours(6);

		// Keyed by full destination path. Entries are tiny and never removed: the
		// set is bounded by the number of distinct assets in a collection.
		private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks =
			new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

		private static readonly JsonSerializerOptions SidecarJsonOptions = new JsonSerializerOptions
		{
			WriteIndented = false,
			PropertyNameCaseInsensitive = true,
		};

		private readonly HttpClient _http;
		private readonly string _cacheDirectory;
		private readonly Action<string> _log;

		/// <summary>
		/// Create a downloader.
		/// </summary>
		/// <param name="http">Client used for every request. Do not mutate it concurrently.</param>
		/// <param name="cacheDirectory">
		/// Root for relative destination paths. Absolute destinations are used as-is.
		/// </param>
		/// <param name="log">Optional diagnostic sink. It never receives URLs with query strings.</param>
		public FileDownloader(HttpClient http, string cacheDirectory, Action<string> log = null)
		{
			_http = http ?? throw new ArgumentNullException(nameof(http));

			if (string.IsNullOrWhiteSpace(cacheDirectory) == true)
				throw new ArgumentException("A cache directory is required.", nameof(cacheDirectory));

			_cacheDirectory = Path.GetFullPath(cacheDirectory);
			_log = log;
		}

		/// <summary>
		/// Download <paramref name="url"/> to <paramref name="destination"/>.
		/// </summary>
		public Task<DownloadResult> DownloadAsync(
			string url,
			string destination,
			long? expectedSize = null,
			string expectedSha1 = null,
			IProgress<(long current, long total)> progress = null,
			CancellationToken cancellationToken = default)
		{
			return DownloadAsync(url, destination, null, expectedSize, expectedSha1, progress, cancellationToken);
		}

		/// <summary>
		/// Download with extra request headers (for example archive.org's Cookie).
		/// Header values are never logged or put into exceptions.
		/// </summary>
		public async Task<DownloadResult> DownloadAsync(
			string url,
			string destination,
			IReadOnlyDictionary<string, string> headers,
			long? expectedSize = null,
			string expectedSha1 = null,
			IProgress<(long current, long total)> progress = null,
			CancellationToken cancellationToken = default)
		{
			Uri uri = ValidateUrl(url);
			string fullDestination = ResolveDestination(destination);
			string partPath = fullDestination + ".part";
			string sidecarPath = partPath + ".json";

			string directory = Path.GetDirectoryName(fullDestination);
			if (string.IsNullOrEmpty(directory) == false)
			{
				try
				{
					Directory.CreateDirectory(directory);
				}
				catch (IOException exception) when (IsDiskFull(exception))
				{
					throw CreateDiskFullException(exception, fullDestination, fullDestination, 0, 0);
				}
			}

			SemaphoreSlim gate = DestinationLocks.GetOrAdd(fullDestination, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				return await DownloadCoreAsync(
					uri,
					fullDestination,
					partPath,
					sidecarPath,
					expectedSize,
					expectedSha1,
					headers,
					progress,
					cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				gate.Release();
			}
		}

		/// <summary>
		/// Download several items with bounded parallelism. Results preserve input
		/// order. Downloads sharing a destination are still serialised by the
		/// per-destination lock.
		/// </summary>
		public async Task<DownloadResult[]> DownloadManyAsync(
			IEnumerable<DownloadJob> jobs,
			int maxConcurrency = 2,
			CancellationToken cancellationToken = default)
		{
			if (jobs == null)
				throw new ArgumentNullException(nameof(jobs));
			if (maxConcurrency < 1)
				throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "At least one concurrent download is required.");

			DownloadJob[] list = jobs.ToArray();
			DownloadResult[] results = new DownloadResult[list.Length];
			if (list.Length == 0)
				return results;

			using SemaphoreSlim slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
			Task[] tasks = new Task[list.Length];

			for (int index = 0; index < list.Length; ++index)
			{
				int captured = index;
				tasks[index] = DownloadOneAsync(captured);
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);

			return results;

			async Task DownloadOneAsync(int index)
			{
				await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					DownloadJob job = list[index];
					results[index] = await DownloadAsync(
						job.Url,
						job.Destination,
						job.Headers,
						job.ExpectedSize,
						job.ExpectedSha1,
						null,
						cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					slots.Release();
				}
			}
		}

		private async Task<DownloadResult> DownloadCoreAsync(
			Uri uri,
			string destination,
			string partPath,
			string sidecarPath,
			long? expectedSize,
			string expectedSha1,
			IReadOnlyDictionary<string, string> headers,
			IProgress<(long current, long total)> progress,
			CancellationToken cancellationToken)
		{
			int attempt = 0;

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				attempt += 1;

				try
				{
					return await DownloadOnceAsync(
						uri,
						destination,
						partPath,
						sidecarPath,
						expectedSize,
						expectedSha1,
						headers,
						progress,
						cancellationToken).ConfigureAwait(false);
				}
				catch (StatusException exception)
				{
					if (exception.Retryable == false || attempt >= MaxAttempts)
						throw exception.ToHttpRequestException();

					await RetryDelayAsync(attempt, exception.RetryAfter, destination, $"HTTP {(int)exception.StatusCode}", cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (OperationCanceledException exception)
				{
					if (attempt >= MaxAttempts)
						throw new TimeoutException($"Download timed out after {MaxAttempts} attempts for {Describe(uri)}.", exception);

					await RetryDelayAsync(attempt, null, destination, "timeout", cancellationToken).ConfigureAwait(false);
				}
				catch (IOException exception) when (IsDiskFull(exception))
				{
					// DownloadOnceAsync has already turned this into a clear message.
					throw;
				}
				catch (HttpRequestException exception)
				{
					if (attempt >= MaxAttempts)
						throw new HttpRequestException(
							$"Download failed after {MaxAttempts} attempts for {Describe(uri)}: {Sanitize(exception.Message, uri)}",
							null,
							exception.StatusCode);

					await RetryDelayAsync(attempt, null, destination, "network error", cancellationToken).ConfigureAwait(false);
				}
				catch (IOException exception)
				{
					if (attempt >= MaxAttempts)
						throw new IOException($"Download failed after {MaxAttempts} attempts for {Describe(uri)}: {Sanitize(exception.Message, uri)}", null);

					await RetryDelayAsync(attempt, null, destination, "I/O error", cancellationToken).ConfigureAwait(false);
				}
			}
		}

		private async Task<DownloadResult> DownloadOnceAsync(
			Uri uri,
			string destination,
			string partPath,
			string sidecarPath,
			long? expectedSize,
			string expectedSha1,
			IReadOnlyDictionary<string, string> headers,
			IProgress<(long current, long total)> progress,
			CancellationToken cancellationToken)
		{
			bool partExists = File.Exists(partPath);
			long existing = partExists ? new FileInfo(partPath).Length : 0;
			PartState state = partExists ? ReadState(sidecarPath) : null;
			bool resuming = partExists;

			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);

			if (headers != null)
			{
				foreach (KeyValuePair<string, string> header in headers)
				{
					if (string.IsNullOrWhiteSpace(header.Key) == true || header.Value == null)
						continue;
					if (string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase) == true ||
						string.Equals(header.Key, "If-Range", StringComparison.OrdinalIgnoreCase) == true)
						continue;

					request.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
			}

			// Ranges and Content-Length refer to the transferred bytes. Ask for the
			// unencoded representation unless the caller explicitly chose otherwise.
			if (request.Headers.AcceptEncoding.Count == 0)
				request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

			if (resuming == true)
			{
				request.Headers.Range = new RangeHeaderValue(existing, null);
				ApplyIfRange(request, state);
				Log($"Resuming \"{destination}\" at {existing} bytes.");
			}
			else
			{
				Log($"Downloading \"{destination}\".");
			}

			using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(DownloadTimeout);

			using HttpResponseMessage response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
				.ConfigureAwait(false);

			HttpStatusCode status = response.StatusCode;

			if (status == HttpStatusCode.RequestedRangeNotSatisfiable && resuming == true)
			{
				// The part already covers the resource. Trust it here; the integrity
				// checks below still decide whether it is usable.
				Log($"Server reports \"{destination}\" is already complete.");
				response.Dispose();
				return await FinishAsync(partPath, destination, sidecarPath, expectedSize, expectedSha1, cancellationToken).ConfigureAwait(false);
			}

			if (status == HttpStatusCode.RequestTimeout ||
				status == HttpStatusCode.TooManyRequests ||
				(int)status >= 500)
			{
				throw new StatusException(
					status,
					true,
					GetRetryAfter(response),
					$"HTTP {(int)status} {status} for {Describe(uri)}.");
			}

			if (response.IsSuccessStatusCode == false)
			{
				throw new StatusException(
					status,
					false,
					null,
					$"HTTP {(int)status} {status} for {Describe(uri)}.");
			}

			bool append = resuming == true && status == HttpStatusCode.PartialContent;
			long total = expectedSize ?? 0;
			long? responseTotal = null;

			if (status == HttpStatusCode.PartialContent)
			{
				ContentRangeHeaderValue contentRange = response.Content.Headers.ContentRange;
				long contentStart = contentRange?.From ?? existing;

				if (contentStart != existing)
				{
					if (contentStart == 0)
					{
						// Server ignored the offset but still sent the whole object.
						append = false;
						existing = 0;
					}
					else
					{
						// We cannot splice this body into the part. Throw the part away
						// and let the next attempt start clean.
						TryDelete(partPath);
						TryDelete(sidecarPath);
						throw new StatusException(
							status,
							true,
							null,
							$"Unexpected Content-Range for {Describe(uri)}.");
					}
				}

				if (append == true && ValidatorChanged(state, response) == true)
				{
					// A server that honours If-Range should have sent 200 here.
					// This covers the ones that do not.
					TryDelete(partPath);
					TryDelete(sidecarPath);
					throw new StatusException(
						status,
						true,
						null,
						$"Validator changed for {Describe(uri)}.");
				}

				responseTotal = contentRange?.Length;
			}
			else
			{
				// 200 (a normal response, or a server that ignored our Range).
				append = false;
				existing = 0;
				responseTotal = response.Content.Headers.ContentLength;
			}

			long? contentLength = response.Content.Headers.ContentLength;
			long resolvedTotal = expectedSize ??
				responseTotal ??
				(append == true && contentLength.HasValue ? existing + contentLength.Value : contentLength) ??
				(append == true ? state?.TotalBytes : null) ??
				0;

			if (resolvedTotal > 0)
				total = resolvedTotal;

			PartState newState = new PartState
			{
				ETag = response.Headers.ETag?.ToString() ?? (append == true ? state?.ETag : null),
				LastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture) ??
					(append == true ? state?.LastModified : null),
				TotalBytes = resolvedTotal > 0 ? resolvedTotal : (long?)null,
			};

			try
			{
				WriteState(sidecarPath, newState);
			}
			catch (IOException exception) when (IsDiskFull(exception))
			{
				throw CreateDiskFullException(exception, partPath, destination, resolvedTotal, existing);
			}

			long current = append == true ? existing : 0;
			long lastReport = Stopwatch.GetTimestamp();
			byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

			try
			{
				FileMode mode = append == true ? FileMode.Append : FileMode.Create;

				try
				{
					await using FileStream part = new FileStream(
						partPath,
						mode,
						FileAccess.Write,
						FileShare.None,
						FileBufferSize,
						FileOptions.Asynchronous);

					await using Stream source = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);

					while (true)
					{
						int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), timeoutSource.Token).ConfigureAwait(false);
						if (read == 0)
							break;

						await part.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token).ConfigureAwait(false);

						current += read;

						if (progress != null)
						{
							long now = Stopwatch.GetTimestamp();
							if (Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds >= ProgressIntervalMilliseconds)
							{
								progress.Report((current, total));
								lastReport = now;
							}
						}
					}

					await part.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
				}
				catch (IOException exception) when (IsDiskFull(exception))
				{
					throw CreateDiskFullException(exception, partPath, destination, total, current);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}

			if (progress != null)
				progress.Report((current, total));

			response.Dispose();

			return await FinishAsync(partPath, destination, sidecarPath, expectedSize, expectedSha1, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<DownloadResult> FinishAsync(
			string partPath,
			string destination,
			string sidecarPath,
			long? expectedSize,
			string expectedSha1,
			CancellationToken cancellationToken)
		{
			if (File.Exists(partPath) == false)
				throw new FileNotFoundException($"The partial download for \"{destination}\" is missing.", partPath);

			long length = new FileInfo(partPath).Length;

			if (expectedSize.HasValue == true && expectedSize.Value != length)
			{
				TryDelete(partPath);
				TryDelete(sidecarPath);
				throw new InvalidDataException(
					$"Size mismatch for \"{destination}\": expected {expectedSize.Value} bytes, got {length} bytes.");
			}

			string actualSha1 = await ComputeSha1Async(partPath, cancellationToken).ConfigureAwait(false);

			string wantedSha1 = string.IsNullOrWhiteSpace(expectedSha1) ? null : expectedSha1.Trim();

			if (wantedSha1 != null &&
				string.Equals(actualSha1, wantedSha1, StringComparison.OrdinalIgnoreCase) == false)
			{
				TryDelete(partPath);
				TryDelete(sidecarPath);
				throw new InvalidDataException(
					$"SHA1 mismatch for \"{destination}\": expected {wantedSha1}, got {actualSha1}.");
			}

			File.Move(partPath, destination, overwrite: true);
			TryDelete(sidecarPath);

			bool verified = expectedSize.HasValue == true || wantedSha1 != null;

			return new DownloadResult(destination, length, actualSha1, verified);
		}

		private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
		{
			await using FileStream stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				FileBufferSize,
				FileOptions.Asynchronous | FileOptions.SequentialScan);

			using SHA1 sha1 = SHA1.Create();
			byte[] hash = await sha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
			return Convert.ToHexString(hash).ToLowerInvariant();
		}

		private async Task RetryDelayAsync(
			int attempt,
			TimeSpan? retryAfter,
			string destination,
			string reason,
			CancellationToken cancellationToken)
		{
			TimeSpan delay = ComputeDelay(attempt, retryAfter);
			Log($"Retrying \"{destination}\" after {reason} in {delay.TotalMilliseconds:0} ms (attempt {attempt + 1} of {MaxAttempts}).");
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}

		private static TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter)
		{
			double milliseconds = BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
			if (milliseconds > MaxRetryDelay.TotalMilliseconds)
				milliseconds = MaxRetryDelay.TotalMilliseconds;

			// +/- 25% jitter so a herd of retries does not hammer the server.
			milliseconds *= 0.75 + (Random.Shared.NextDouble() * 0.5);
			if (milliseconds > MaxRetryDelay.TotalMilliseconds)
				milliseconds = MaxRetryDelay.TotalMilliseconds;

			TimeSpan delay = TimeSpan.FromMilliseconds(milliseconds);

			if (retryAfter.HasValue == true)
			{
				// A server-supplied Retry-After replaces the computed backoff. Only
				// the exponential backoff is capped; the server's delay is honoured.
				delay = retryAfter.Value;
			}

			if (delay < TimeSpan.Zero)
				delay = TimeSpan.Zero;

			return delay;
		}

		private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
		{
			RetryConditionHeaderValue header = response.Headers.RetryAfter;
			if (header == null)
				return null;

			if (header.Delta.HasValue == true)
				return header.Delta.Value;

			if (header.Date.HasValue == true)
			{
				TimeSpan delay = header.Date.Value - DateTimeOffset.UtcNow;
				return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
			}

			return null;
		}

		private static bool ValidatorChanged(PartState state, HttpResponseMessage response)
		{
			if (state == null)
				return false;

			string responseETag = response.Headers.ETag?.ToString();
			if (string.IsNullOrEmpty(state.ETag) == false && string.IsNullOrEmpty(responseETag) == false)
				return string.Equals(state.ETag, responseETag, StringComparison.Ordinal) == false;

			string responseLastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
			if (string.IsNullOrEmpty(state.LastModified) == false && string.IsNullOrEmpty(responseLastModified) == false)
				return string.Equals(state.LastModified, responseLastModified, StringComparison.Ordinal) == false;

			return false;
		}

		private static void ApplyIfRange(HttpRequestMessage request, PartState state)
		{
			if (state == null)
				return;

			if (string.IsNullOrWhiteSpace(state.ETag) == false &&
				EntityTagHeaderValue.TryParse(state.ETag, out EntityTagHeaderValue etag) == true)
			{
				request.Headers.IfRange = new RangeConditionHeaderValue(etag);
				return;
			}

			if (string.IsNullOrWhiteSpace(state.LastModified) == false &&
				DateTimeOffset.TryParse(
					state.LastModified,
					CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
					out DateTimeOffset lastModified) == true)
			{
				request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
			}
		}

		private static PartState ReadState(string path)
		{
			try
			{
				if (File.Exists(path) == false)
					return null;

				string json = File.ReadAllText(path);
				return JsonSerializer.Deserialize<PartState>(json, SidecarJsonOptions);
			}
			catch (Exception exception) when (
				exception is IOException ||
				exception is JsonException ||
				exception is UnauthorizedAccessException ||
				exception is NotSupportedException)
			{
				return null;
			}
		}

		private static void WriteState(string path, PartState state)
		{
			string json = JsonSerializer.Serialize(state, SidecarJsonOptions);
			File.WriteAllText(path, json);
		}

		private static bool IsDiskFull(IOException exception)
		{
			int hresult = exception.HResult & 0xFFFF;
			if (hresult == 28 || hresult == 39 || hresult == 112) // ENOSPC, ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
				return true;

			string message = exception.Message;
			if (string.IsNullOrEmpty(message) == true)
				return false;

			return message.IndexOf("no space left", StringComparison.OrdinalIgnoreCase) >= 0 ||
				message.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0 ||
				message.IndexOf("not enough space", StringComparison.OrdinalIgnoreCase) >= 0 ||
				message.IndexOf("enospc", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static IOException CreateDiskFullException(
			IOException inner,
			string partPath,
			string destination,
			long total,
			long current)
		{
			string filesystem;
			try
			{
				filesystem = Path.GetPathRoot(partPath);
			}
			catch (Exception)
			{
				filesystem = null;
			}

			if (string.IsNullOrEmpty(filesystem) == true)
				filesystem = "the destination filesystem";

			long needed = total > current ? total - current : 0;
			if (needed <= 0)
				needed = BufferSize;

			return new IOException(
				$"Not enough disk space on \"{filesystem}\" for \"{destination}\"; {needed} more bytes are needed.",
				inner);
		}

		private static void TryDelete(string path)
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception exception) when (
				exception is IOException ||
				exception is UnauthorizedAccessException)
			{
				// Best effort. A stale part/sidecar is harmless; a stale destination is not.
			}
		}

		private string ResolveDestination(string destination)
		{
			if (string.IsNullOrWhiteSpace(destination) == true)
				throw new ArgumentException("A destination path is required.", nameof(destination));

			string path = Path.IsPathRooted(destination) == true
				? destination
				: Path.Combine(_cacheDirectory, destination);

			return Path.GetFullPath(path);
		}

		private static Uri ValidateUrl(string url)
		{
			if (string.IsNullOrWhiteSpace(url) == true)
				throw new ArgumentException("A URL is required.", nameof(url));

			if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri) == false ||
				(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			{
				throw new ArgumentException("The URL must be an absolute http or https URI.", nameof(url));
			}

			return uri;
		}

		/// <summary>
		/// Scheme/host/port/path only. Query strings and fragments can contain
		/// credentials and must never reach a log or an exception message.
		/// </summary>
		private static string Describe(Uri uri)
		{
			if (uri == null)
				return "(unknown)";

			string path = string.IsNullOrEmpty(uri.AbsolutePath) == true ? "/" : uri.AbsolutePath;
			string port = uri.IsDefaultPort == true ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

			return $"{uri.Scheme}://{uri.Host}{port}{path}";
		}

		private static string Sanitize(string message, Uri uri)
		{
			if (string.IsNullOrEmpty(message) == true)
				return message;

			string text = message;

			if (string.IsNullOrEmpty(uri.Query) == false)
				text = text.Replace(uri.Query, string.Empty, StringComparison.Ordinal);

			if (string.IsNullOrEmpty(uri.Fragment) == false)
				text = text.Replace(uri.Fragment, string.Empty, StringComparison.Ordinal);

			text = text.Replace(uri.AbsoluteUri, Describe(uri), StringComparison.Ordinal);

			if (string.IsNullOrEmpty(uri.UserInfo) == false)
				text = text.Replace(uri.UserInfo + "@", string.Empty, StringComparison.Ordinal);

			return text;
		}

		private void Log(string message)
		{
			Action<string> log = _log;
			if (log == null)
				return;

			try
			{
				log(message);
			}
			catch (Exception)
			{
				// A broken diagnostic sink must not fail a download.
			}
		}

		// ------------------------------------------------------------------
		// Static convenience API.
		//
		// Download/DownloadAsync stage data in "<filename>.part", resume it with an
		// HTTP Range request, retry with backoff, verify an optional SHA1, and only
		// rename onto the destination once everything checks out, so an interrupted
		// transfer can never look like a finished file. Every failure is reported as
		// System.ApplicationException naming the URL path, the HTTP status and how
		// much data is left in the ".part" file, so the caller needs one catch clause.
		// ------------------------------------------------------------------

		private const int StaticBufferSize = 1024 * 1024;
		private const int StaticProgressIntervalMilliseconds = 200;

		private static readonly TimeSpan StaticBaseRetryDelay = TimeSpan.FromSeconds(1);
		private static readonly TimeSpan StaticMaxRetryDelay = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan StaticMaxRetryAfter = TimeSpan.FromSeconds(60);
		private static readonly TimeSpan StaticStallTimeout = TimeSpan.FromMinutes(5);

		private static readonly HttpClient StaticHttp = CreateStaticHttpClient();

		/// <summary>
		/// Download <paramref name="url"/> to <paramref name="filename"/>.
		/// </summary>
		/// <returns>The length of the finished file in bytes.</returns>
		/// <exception cref="ApplicationException">The download failed; see the message.</exception>
		public static long Download(string url, string filename, DownloadOptions options = null)
		{
			return DownloadAsync(url, filename, options).GetAwaiter().GetResult();
		}

		/// <summary>
		/// Download <paramref name="url"/> to <paramref name="filename"/>.
		///
		/// Data goes to "&lt;filename&gt;.part" and is resumed with a Range request
		/// when that file already exists and the server answers 206. On success the
		/// ".part" file is renamed onto <paramref name="filename"/>; on failure it is
		/// left in place for the next resume (except after a SHA1 mismatch, where it
		/// is deleted). <paramref name="filename"/> is never created by a failure.
		/// </summary>
		/// <returns>The length of the finished file in bytes.</returns>
		/// <exception cref="ApplicationException">The download failed; see the message.</exception>
		/// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
		public static Task<long> DownloadAsync(string url, string filename, DownloadOptions options = null, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(url) == true)
				throw new ArgumentException("A URL is required.", nameof(url));

			if (string.IsNullOrWhiteSpace(filename) == true)
				throw new ArgumentException("A filename is required.", nameof(filename));

			if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri) == false ||
				(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			{
				throw new ArgumentException("The URL must be an absolute http or https URI.", nameof(url));
			}

			string destination = Path.GetFullPath(filename);
			string partPath = destination + ".part";

			string directory = Path.GetDirectoryName(destination);
			if (string.IsNullOrEmpty(directory) == false)
			{
				try
				{
					Directory.CreateDirectory(directory);
				}
				catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
				{
					throw new ApplicationException(
						$"Download failed for \"{Describe(uri)}\" (no HTTP response): cannot create \"{directory}\": {exception.Message}. No partial file was kept.",
						exception);
				}
			}

			return DownloadStaticCoreAsync(uri, destination, partPath, options ?? new DownloadOptions(), ct);
		}

		private static async Task<long> DownloadStaticCoreAsync(Uri uri, string destination, string partPath, DownloadOptions options, CancellationToken ct)
		{
			int maxAttempts = options.MaxAttempts < 1 ? 1 : options.MaxAttempts;
			AttemptState state = new AttemptState();

			SemaphoreSlim gate = DestinationLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(ct).ConfigureAwait(false);

			try
			{
				int attempt = 0;

				while (true)
				{
					ct.ThrowIfCancellationRequested();
					++attempt;

					try
					{
						return await DownloadStaticAttemptAsync(uri, destination, partPath, options, state, ct).ConfigureAwait(false);
					}
					catch (AttemptFailedException exception)
					{
						if (exception.DeletePart == true)
							TryDelete(partPath);

						if (exception.Retryable == false || attempt >= maxAttempts)
							throw CreateStaticFailure(uri, state.Status, partPath, exception.Message, attempt, exception);

						await DelayStaticRetryAsync(attempt, exception.RetryAfter, ct).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested == true)
					{
						throw;
					}
					catch (OperationCanceledException)
					{
						if (attempt >= maxAttempts)
							throw CreateStaticFailure(uri, state.Status, partPath, "the transfer stalled", attempt, null);

						await DelayStaticRetryAsync(attempt, null, ct).ConfigureAwait(false);
					}
					catch (HttpRequestException exception)
					{
						string detail = Sanitize(exception.Message, uri);

						if (attempt >= maxAttempts)
							throw CreateStaticFailure(uri, state.Status, partPath, detail, attempt, exception);

						await DelayStaticRetryAsync(attempt, null, ct).ConfigureAwait(false);
					}
					catch (IOException exception) when (IsDiskFull(exception) == true)
					{
						// Retrying cannot make room, so report immediately.
						throw CreateStaticFailure(uri, state.Status, partPath, exception.Message, attempt, exception);
					}
					catch (IOException exception)
					{
						string detail = Sanitize(exception.Message, uri);

						if (attempt >= maxAttempts)
							throw CreateStaticFailure(uri, state.Status, partPath, detail, attempt, exception);

						await DelayStaticRetryAsync(attempt, null, ct).ConfigureAwait(false);
					}
					catch (UnauthorizedAccessException exception)
					{
						throw CreateStaticFailure(uri, state.Status, partPath, exception.Message, attempt, exception);
					}
				}
			}
			finally
			{
				gate.Release();
			}
		}

		private static async Task<long> DownloadStaticAttemptAsync(
			Uri uri,
			string destination,
			string partPath,
			DownloadOptions options,
			AttemptState state,
			CancellationToken ct)
		{
			long existing = 0;
			if (File.Exists(partPath) == true)
				existing = new FileInfo(partPath).Length;

			long expected = options.ExpectedSize > 0 ? options.ExpectedSize : 0;

			if (expected > 0 && existing > expected)
			{
				TryDelete(partPath);
				throw new AttemptFailedException($"the partial file is {existing} bytes, more than the expected {expected} bytes", false, false);
			}

			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);

			// Byte ranges and Content-Length refer to the transferred bytes.
			request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

			if (string.IsNullOrEmpty(options.AuthCookie) == false)
				request.Headers.TryAddWithoutValidation("Cookie", options.AuthCookie);

			if (existing > 0)
				request.Headers.Range = new RangeHeaderValue(existing, null);

			using CancellationTokenSource attemptSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
			attemptSource.CancelAfter(StaticStallTimeout);

			using HttpResponseMessage response = await StaticHttp
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptSource.Token)
				.ConfigureAwait(false);

			int status = (int)response.StatusCode;
			state.Status = status;

			if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
			{
				// The part already covers the resource; the checks below decide
				// whether it is usable.
				if (expected > 0 && existing == expected)
					return await FinishStaticAsync(destination, partPath, options, ct).ConfigureAwait(false);

				TryDelete(partPath);
				throw new AttemptFailedException($"the server rejected the resume offset (HTTP {status})", true, false);
			}

			if (status == 408 || status == 429 || status >= 500)
				throw new AttemptFailedException($"the server returned HTTP {status}", true, false, GetRetryAfter(response));

			if (response.IsSuccessStatusCode == false)
				throw new AttemptFailedException($"the server returned HTTP {status}", false, false);

			bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
			long total = expected;

			if (response.StatusCode == HttpStatusCode.PartialContent)
			{
				ContentRangeHeaderValue contentRange = response.Content.Headers.ContentRange;
				long from = contentRange?.From ?? existing;

				if (from != existing)
				{
					TryDelete(partPath);

					if (from != 0)
						throw new AttemptFailedException($"the server resumed from byte {from} instead of {existing}", true, false);

					// Server ignored the offset and sent the whole object.
					append = false;
				}

				if (total == 0 && contentRange?.Length != null)
					total = contentRange.Length.Value;
			}
			else
			{
				// 200: a normal response, or a server that ignored our Range.
				append = false;

				long? contentLength = response.Content.Headers.ContentLength;
				if (total == 0 && contentLength.HasValue == true)
					total = contentLength.Value;
			}

			long current = append == true ? existing : 0;
			long lastReport = Stopwatch.GetTimestamp();
			byte[] buffer = ArrayPool<byte>.Shared.Rent(StaticBufferSize);

			try
			{
				ReportStaticProgress(options.Progress, current, total, ref lastReport, true);

				await using (FileStream part = new FileStream(
					partPath,
					append == true ? FileMode.Append : FileMode.Create,
					FileAccess.Write,
					FileShare.None,
					FileBufferSize,
					FileOptions.Asynchronous))
				{
					await using (Stream source = await response.Content.ReadAsStreamAsync(attemptSource.Token).ConfigureAwait(false))
					{
						while (true)
						{
							int read = await source.ReadAsync(buffer.AsMemory(0, StaticBufferSize), attemptSource.Token).ConfigureAwait(false);
							if (read == 0)
								break;

							await part.WriteAsync(buffer.AsMemory(0, read), attemptSource.Token).ConfigureAwait(false);

							current += read;

							// Data moved, so push the stall watchdog out again.
							attemptSource.CancelAfter(StaticStallTimeout);

							ReportStaticProgress(options.Progress, current, total, ref lastReport, false);
						}
					}

					await part.FlushAsync(attemptSource.Token).ConfigureAwait(false);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}

			ReportStaticProgress(options.Progress, current, total, ref lastReport, true);

			if (total > 0 && current > total)
			{
				TryDelete(partPath);
				throw new AttemptFailedException($"received {current} bytes but expected only {total}", false, false);
			}

			if (total > 0 && current < total)
				throw new AttemptFailedException($"the transfer ended at {current} of {total} bytes", true, false);

			return await FinishStaticAsync(destination, partPath, options, ct).ConfigureAwait(false);
		}

		private static async Task<long> FinishStaticAsync(string destination, string partPath, DownloadOptions options, CancellationToken ct)
		{
			if (File.Exists(partPath) == false)
				throw new AttemptFailedException("the partial file disappeared", false, false);

			long length = new FileInfo(partPath).Length;

			if (options.ExpectedSize > 0 && length != options.ExpectedSize)
			{
				// Too long cannot be resumed; too short can.
				throw new AttemptFailedException(
					$"the file is {length} bytes, expected {options.ExpectedSize}",
					retryable: length < options.ExpectedSize,
					deletePart: length > options.ExpectedSize);
			}

			if (string.IsNullOrWhiteSpace(options.ExpectedSha1) == false)
			{
				string actual = await ComputeSha1Async(partPath, ct).ConfigureAwait(false);
				string wanted = options.ExpectedSha1.Trim();

				if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase) == false)
					throw new AttemptFailedException($"SHA1 mismatch, expected {wanted} but got {actual}", false, true);
			}

			// Same directory, so this is rename(2): <filename> appears complete or not at all.
			File.Move(partPath, destination, overwrite: true);

			return length;
		}

		private static void ReportStaticProgress(Action<long, long> progress, long current, long total, ref long lastReport, bool force)
		{
			if (progress == null)
				return;

			long now = Stopwatch.GetTimestamp();
			if (force == false && Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds < StaticProgressIntervalMilliseconds)
				return;

			lastReport = now;

			try
			{
				progress(current, total > 0 ? total : 0);
			}
			catch (Exception)
			{
				// A diagnostic sink must not fail a download.
			}
		}

		private static async Task DelayStaticRetryAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
		{
			TimeSpan delay = retryAfter ?? ComputeStaticDelay(attempt);

			if (delay < TimeSpan.Zero)
				delay = TimeSpan.Zero;

			// A long Retry-After must not park a daemon indefinitely.
			if (delay > StaticMaxRetryAfter)
				delay = StaticMaxRetryAfter;

			await Task.Delay(delay, ct).ConfigureAwait(false);
		}

		private static TimeSpan ComputeStaticDelay(int attempt)
		{
			double milliseconds = StaticBaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
			if (milliseconds > StaticMaxRetryDelay.TotalMilliseconds)
				milliseconds = StaticMaxRetryDelay.TotalMilliseconds;

			// +/- 25% jitter so a herd of retries does not hammer the server.
			milliseconds *= 0.75 + (Random.Shared.NextDouble() * 0.5);

			if (milliseconds > StaticMaxRetryDelay.TotalMilliseconds)
				milliseconds = StaticMaxRetryDelay.TotalMilliseconds;

			return TimeSpan.FromMilliseconds(milliseconds);
		}

		private static ApplicationException CreateStaticFailure(Uri uri, int? status, string partPath, string detail, int attempts, Exception inner)
		{
			bool partExists = false;
			long leftover = 0;

			try
			{
				if (File.Exists(partPath) == true)
				{
					partExists = true;
					leftover = new FileInfo(partPath).Length;
				}
			}
			catch (Exception)
			{
				partExists = false;
			}

			string statusText = status.HasValue == true ? $"HTTP {status.Value}" : "no HTTP response";
			string partText = partExists == true
				? $"{leftover} bytes remain in \"{partPath}\""
				: "no partial file was kept";

			return new ApplicationException(
				$"Download failed for \"{Describe(uri)}\" ({statusText}, {attempts} attempt(s)): {detail}. {partText}.",
				inner);
		}

		private static HttpClient CreateStaticHttpClient()
		{
			SocketsHttpHandler handler = new SocketsHttpHandler
			{
				UseCookies = false,
				AllowAutoRedirect = true,
				AutomaticDecompression = DecompressionMethods.None,
				ConnectTimeout = TimeSpan.FromSeconds(60),
				PooledConnectionLifetime = TimeSpan.FromMinutes(10),
			};

			HttpClient client = new HttpClient(handler)
			{
				// StaticStallTimeout is enforced per attempt instead.
				Timeout = Timeout.InfiniteTimeSpan,
			};

			client.DefaultRequestHeaders.UserAgent.ParseAdd("MAME-AO/1.0 (+https://github.com/sam-ludlow/mame-ao)");

			return client;
		}

		private sealed class AttemptState
		{
			public int? Status { get; set; }
		}

		private sealed class AttemptFailedException : Exception
		{
			public AttemptFailedException(string message, bool retryable, bool deletePart, TimeSpan? retryAfter = null)
				: base(message)
			{
				Retryable = retryable;
				DeletePart = deletePart;
				RetryAfter = retryAfter;
			}

			public bool Retryable { get; }
			public bool DeletePart { get; }
			public TimeSpan? RetryAfter { get; }
		}

		private sealed class PartState
		{
			public string ETag { get; set; }
			public string LastModified { get; set; }
			public long? TotalBytes { get; set; }
		}

		private sealed class StatusException : Exception
		{
			public StatusException(HttpStatusCode statusCode, bool retryable, TimeSpan? retryAfter, string message)
				: base(message)
			{
				StatusCode = statusCode;
				Retryable = retryable;
				RetryAfter = retryAfter;
			}

			public HttpStatusCode StatusCode { get; }
			public bool Retryable { get; }
			public TimeSpan? RetryAfter { get; }

			public HttpRequestException ToHttpRequestException()
			{
				return new HttpRequestException(Message, null, StatusCode);
			}
		}
	}
}
