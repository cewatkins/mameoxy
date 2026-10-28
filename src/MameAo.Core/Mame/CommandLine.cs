using System;
using System.Collections.Generic;
using System.Text;

namespace Spludlow.MameAO
{
	/// <summary>
	/// Tokenises the small amount of free-form command text that survives in the
	/// port (the MameArguments config value and the legacy Windows-only cores).
	/// Everything else builds a <c>List&lt;string&gt;</c> and hands it to
	/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so no shell
	/// ever parses a path.
	/// </summary>
	public static class CommandLine
	{
		public static IReadOnlyList<string> Split(string text)
		{
			List<string> result = new List<string>();

			if (string.IsNullOrWhiteSpace(text) == true)
				return result;

			StringBuilder current = new StringBuilder();
			bool inToken = false;
			char quote = '\0';

			foreach (char c in text)
			{
				if (quote != '\0')
				{
					if (c == quote)
						quote = '\0';
					else
						current.Append(c);
					continue;
				}

				if (c == '"' || c == '\'')
				{
					quote = c;
					inToken = true;
					continue;
				}

				if (char.IsWhiteSpace(c) == true)
				{
					if (inToken == true)
					{
						result.Add(current.ToString());
						current.Clear();
						inToken = false;
					}
					continue;
				}

				current.Append(c);
				inToken = true;
			}

			if (inToken == true)
				result.Add(current.ToString());

			return result;
		}

		/// <summary>Append <paramref name="text"/> as tokens to <paramref name="arguments"/>.</summary>
		public static void Append(IList<string> arguments, string text)
		{
			foreach (string token in Split(text))
				arguments.Add(token);
		}
	}
}
