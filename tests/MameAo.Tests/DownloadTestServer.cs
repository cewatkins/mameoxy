using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MameAo.Tests;

internal sealed record RecordedRequest(string Method, string Path, string Range, string IfRange);

internal sealed record ScriptedStatus(int StatusCode, double? RetryAfterSeconds = null);

/// <summary>
/// Minimal loopback HTTP server for downloader tests. Supports Range, ETag,
/// Last-Modified, scripted status responses, a single truncated response and
/// throttled bodies. Never talks to the internet.
/// </summary>
internal sealed class DownloadTestServer : IDisposable
{
	private readonly HttpListener _listener;
	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
	private readonly Task _acceptLoop;
	private readonly ConcurrentQueue<ScriptedStatus> _script = new ConcurrentQueue<ScriptedStatus>();
	private int _truncateFirstResponseAfter = -1;
	private long _requestCount;

	public DownloadTestServer()
	{
		_listener = StartListener(out string baseUrl);
		BaseUrl = baseUrl;
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
	}

	public string BaseUrl { get; }

	public byte[] Content { get; set; } = Array.Empty<byte>();

	public string ETag { get; set; } = "\"test-etag\"";

	public string LastModified { get; set; } = "Wed, 21 Oct 2015 07:28:00 GMT";

	public bool AllowRanges { get; set; } = true;

	public int ThrottleChunkSize { get; set; }

	public int ThrottleDelayMilliseconds { get; set; }

	public int TruncateFirstResponseAfter
	{
		get => Volatile.Read(ref _truncateFirstResponseAfter);
		set => Volatile.Write(ref _truncateFirstResponseAfter, value);
	}

	public ConcurrentQueue<RecordedRequest> Requests { get; } = new ConcurrentQueue<RecordedRequest>();

	public int RequestCount => (int)Interlocked.Read(ref _requestCount);

	public void EnqueueStatus(int statusCode, double? retryAfterSeconds = null)
	{
		_script.Enqueue(new ScriptedStatus(statusCode, retryAfterSeconds));
	}

	private static HttpListener StartListener(out string baseUrl)
	{
		for (int attempt = 0; attempt < 20; ++attempt)
		{
			int port = GetFreePort();
			string prefix = $"http://127.0.0.1:{port}/";
			HttpListener listener = new HttpListener();
			listener.Prefixes.Add(prefix);

			try
			{
				listener.Start();
				baseUrl = prefix.TrimEnd('/');
				return listener;
			}
			catch (HttpListenerException)
			{
				listener.Close();
			}
		}

		throw new InvalidOperationException("Could not start a loopback HTTP listener.");
	}

	private static int GetFreePort()
	{
		TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (cancellationToken.IsCancellationRequested == false)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (Exception exception) when (
				cancellationToken.IsCancellationRequested == true ||
				_listener.IsListening == false ||
				exception is HttpListenerException)
			{
				return;
			}

			_ = Task.Run(() => HandleAsync(context));
		}
	}

	private async Task HandleAsync(HttpListenerContext context)
	{
		Interlocked.Increment(ref _requestCount);

		HttpListenerRequest request = context.Request;
		HttpListenerResponse response = context.Response;

		Requests.Enqueue(new RecordedRequest(
			request.HttpMethod,
			request.Url?.AbsolutePath ?? string.Empty,
			request.Headers["Range"] ?? string.Empty,
			request.Headers["If-Range"] ?? string.Empty));

		try
		{
			if (_script.TryDequeue(out ScriptedStatus? scripted) == true && scripted != null)
			{
				byte[] body = Encoding.UTF8.GetBytes($"status {scripted.StatusCode}");
				response.StatusCode = scripted.StatusCode;
				response.ContentLength64 = body.Length;
				response.AddHeader("ETag", ETag);

				if (scripted.RetryAfterSeconds.HasValue == true)
				{
					response.AddHeader(
						"Retry-After",
						scripted.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture));
				}

				await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
				response.Close();
				return;
			}

			await ServeContentAsync(response, request).ConfigureAwait(false);
		}
		catch (Exception)
		{
			try
			{
				response.Abort();
			}
			catch (Exception)
			{
				// Already closed.
			}
		}
	}

	private async Task ServeContentAsync(HttpListenerResponse response, HttpListenerRequest request)
	{
		byte[] content = Content;
		long offset = 0;
		bool partial = false;

		string rangeHeader = request.Headers["Range"] ?? string.Empty;
		string ifRange = request.Headers["If-Range"] ?? string.Empty;

		bool ifRangeMatches = string.IsNullOrEmpty(ifRange) == true ||
			string.Equals(ifRange, ETag, StringComparison.Ordinal) == true ||
			string.Equals(ifRange, LastModified, StringComparison.Ordinal) == true;

		if (AllowRanges == true &&
			string.IsNullOrEmpty(rangeHeader) == false &&
			ifRangeMatches == true &&
			TryParseRange(rangeHeader, out long start) == true)
		{
			if (start >= content.LongLength)
			{
				response.StatusCode = 416;
				response.AddHeader("Content-Range", $"bytes */{content.LongLength}");
				response.AddHeader("ETag", ETag);
				response.ContentLength64 = 0;
				response.Close();
				return;
			}

			offset = start;
			partial = true;
		}

		long remaining = content.LongLength - offset;
		response.StatusCode = partial == true ? 206 : 200;
		response.AddHeader("ETag", ETag);
		response.AddHeader("Last-Modified", LastModified);
		response.AddHeader("Accept-Ranges", AllowRanges == true ? "bytes" : "none");

		if (partial == true)
			response.AddHeader("Content-Range", $"bytes {offset}-{content.LongLength - 1}/{content.LongLength}");

		response.ContentLength64 = remaining;

		long truncateAfter = Interlocked.Exchange(ref _truncateFirstResponseAfter, -1);
		if (truncateAfter > 0)
			response.KeepAlive = false;

		long sent = 0;
		int chunkSize = ThrottleChunkSize > 0 ? ThrottleChunkSize : 64 * 1024;

		if (truncateAfter == 0)
		{
			response.Abort();
			return;
		}

		while (sent < remaining)
		{
			int count = (int)Math.Min(chunkSize, remaining - sent);

			if (truncateAfter > 0 && sent + count > truncateAfter)
				count = (int)Math.Max(0, truncateAfter - sent);

			if (count > 0)
			{
				await response.OutputStream
					.WriteAsync(content.AsMemory((int)(offset + sent), count))
					.ConfigureAwait(false);
				await response.OutputStream.FlushAsync().ConfigureAwait(false);
				sent += count;
			}

			if (truncateAfter > 0 && sent >= truncateAfter)
			{
				// Graceful close with an advertised Content-Length that is longer
				// than what was sent: the client sees a premature EOF and keeps
				// the bytes it already received.
				await Task.Delay(100).ConfigureAwait(false);
				try
				{
					response.OutputStream.Close();
				}
				catch (Exception)
				{
					// The client may already have gone away.
				}

				return;
			}

			if (ThrottleDelayMilliseconds > 0)
				await Task.Delay(ThrottleDelayMilliseconds).ConfigureAwait(false);
		}

		response.Close();
	}

	private static bool TryParseRange(string header, out long start)
	{
		start = 0;

		const string prefix = "bytes=";
		if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == false)
			return false;

		string spec = header.Substring(prefix.Length);
		int dash = spec.IndexOf('-');
		if (dash <= 0)
			return false;

		return long.TryParse(
			spec.AsSpan(0, dash),
			NumberStyles.Integer,
			CultureInfo.InvariantCulture,
			out start) && start >= 0;
	}

	public void Dispose()
	{
		_cancellation.Cancel();

		try
		{
			_listener.Stop();
		}
		catch (Exception)
		{
			// Already stopped.
		}

		try
		{
			_listener.Close();
		}
		catch (Exception)
		{
			// Already closed.
		}

		try
		{
			_acceptLoop.Wait(TimeSpan.FromSeconds(2));
		}
		catch (Exception)
		{
			// Cancellation/abort.
		}

		_cancellation.Dispose();
	}
}
