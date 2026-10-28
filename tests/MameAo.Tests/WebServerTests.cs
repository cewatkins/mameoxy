using System;
using System.Linq;
using System.Net;
using Spludlow.MameAO;
using Xunit;

namespace MameAo.Tests
{
	public class WebServerTests
	{
		private static int FreePort()
		{
			System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
			probe.Start();
			int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		[Fact]
		public void Loopback_yields_a_single_usable_prefix()
		{
			int port = FreePort();
			string address = $"http://127.0.0.1:{port}/";

			string[] prefixes = WebServer.ListenerPrefixes(address);

			Assert.Equal(new[] { address }, prefixes);

			// The prefix list must actually bind: HttpListener validates prefixes at
			// Start(), not when they are added.
			HttpListener listener = new HttpListener();
			foreach (string prefix in prefixes)
				listener.Prefixes.Add(prefix);

			listener.Start();
			listener.Stop();
		}

		[Fact]
		public void Wildcard_binds_any_address()
		{
			string[] prefixes = WebServer.ListenerPrefixes("http://*:12390/");

			Assert.Equal(new[] { "http://*:12390/" }, prefixes);
		}

		[Fact]
		public void Ipv6_is_refused_with_an_actionable_message()
		{
			if (OperatingSystem.IsWindows() == true)
				return;

			ApplicationException e = Assert.Throws<ApplicationException>(
				() => WebServer.ListenerPrefixes("http://[::1]:12380/"));

			Assert.Contains("IPv6", e.Message);
			Assert.Contains("127.0.0.1", e.Message);
		}
	}
}
