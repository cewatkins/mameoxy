using System;

using Spludlow.MameAO;

using Xunit;

namespace MameAo.Tests
{
	public class VersionTests
	{
		[Theory]
		[InlineData("v0.10.0", "0.10")]
		[InlineData("0.10.0", "0.10")]
		[InlineData("v0.10", "0.10")]
		[InlineData("v1.2.3", "1.2")]
		[InlineData("0.10", "0.10")]
		[InlineData("v0.11.0-rc1", "0.11")]
		public void Release_tags_compare_by_major_minor(string tag, string expected)
		{
			Assert.Equal(expected, MameAOProcessor.NormaliseVersion(tag));
		}

		[Fact]
		public void Empty_tag_is_empty()
		{
			Assert.Equal("", MameAOProcessor.NormaliseVersion(""));
			Assert.Equal("", MameAOProcessor.NormaliseVersion(null));
		}
	}
}
