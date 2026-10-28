using System;
using System.Collections.Generic;

using Spludlow.MameAO;

namespace MameAo.Tests;

public class CommandLineTests
{
    [Fact]
    public void Split_Empty()
    {
        Assert.Empty(CommandLine.Split(null));
        Assert.Empty(CommandLine.Split(""));
        Assert.Empty(CommandLine.Split("   \t "));
    }

    [Fact]
    public void Split_SimpleFlags()
    {
        Assert.Equal(new[] { "-video", "none", "-sound", "none" }, CommandLine.Split("-video none -sound none"));
    }

    [Fact]
    public void Split_KeepsQuotedPathsTogether()
    {
        Assert.Equal(
            new[] { "-rompath", "/home/me/My ROMs", "a2600" },
            CommandLine.Split("-rompath \"/home/me/My ROMs\" a2600"));

        Assert.Equal(
            new[] { "one", "two words", "three" },
            CommandLine.Split("one 'two words' three"));
    }

    [Fact]
    public void Split_CollapsesWhitespace()
    {
        Assert.Equal(new[] { "a", "b" }, CommandLine.Split("  a\t\tb\n"));
    }

    [Fact]
    public void Append_AddsToList()
    {
        List<string> arguments = new List<string> { "a2600" };

        CommandLine.Append(arguments, "-window -cheat");

        Assert.Equal(new[] { "a2600", "-window", "-cheat" }, arguments);
    }

    [Fact]
    public void Append_IgnoresEmptyText()
    {
        List<string> arguments = new List<string>();

        CommandLine.Append(arguments, null);
        CommandLine.Append(arguments, "  ");

        Assert.Empty(arguments);
    }
}
