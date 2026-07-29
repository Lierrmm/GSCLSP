using GSCLSP.Core.Models;

namespace GSCLSP.Core.Tests;

public class GscCompiledScriptDetectorTests
{
    private const char Magic = (char)0x80;
    private const char Replacement = (char)0xFFFD;

    [Fact]
    public void IsCompiledText_RawMagic_ReturnsTrue()
    {
        Assert.True(GscCompiledScriptDetector.IsCompiledText(Magic + "GSCbinary"));
    }

    [Fact]
    public void IsCompiledText_DecodedMagic_ReturnsTrue()
    {
        Assert.True(GscCompiledScriptDetector.IsCompiledText(Replacement + "GSCbinary"));
    }

    [Fact]
    public void IsCompiledText_BinaryGarbageWithoutMagic_ReturnsTrue()
    {
        var text = new string('a', 100) + new string('\0', 20) + new string('b', 100);
        Assert.True(GscCompiledScriptDetector.IsCompiledText(text));
    }

    [Fact]
    public void IsCompiledText_NormalSource_ReturnsFalse()
    {
        const string source = """
            #include maps\mp\_utility;

            init()
            {
                level thread on_player_connect();
            }
            """;

        Assert.False(GscCompiledScriptDetector.IsCompiledText(source));
    }

    [Fact]
    public void IsCompiledText_ShortSource_ReturnsFalse()
    {
        Assert.False(GscCompiledScriptDetector.IsCompiledText("init(){}"));
    }

    [Fact]
    public void IsCompiledText_Empty_ReturnsFalse()
    {
        Assert.False(GscCompiledScriptDetector.IsCompiledText(string.Empty));
    }

    [Fact]
    public void IsCompiledText_WhitespaceOnly_ReturnsFalse()
    {
        Assert.False(GscCompiledScriptDetector.IsCompiledText("  \r\n\t\r\n  "));
    }

    [Fact]
    public void IsCompiledText_MagicPrefixOnly_ReturnsFalse()
    {
        Assert.False(GscCompiledScriptDetector.IsCompiledText("GSC init(){}"));
    }

    [Fact]
    public void IsCompiledFile_MagicBytes_ReturnsTrue()
    {
        var path = WriteTempFile([0x80, (byte)'G', (byte)'S', (byte)'C', 0x01, 0x02, 0x03]);
        try
        {
            Assert.True(GscCompiledScriptDetector.IsCompiledFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCompiledFile_NulBytesWithoutMagic_ReturnsTrue()
    {
        var path = WriteTempFile([(byte)'i', (byte)'n', (byte)'i', (byte)'t', 0x00, (byte)'x']);
        try
        {
            Assert.True(GscCompiledScriptDetector.IsCompiledFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCompiledFile_PlainSource_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gsc");
        File.WriteAllText(path, "init()\n{\n    level.foo = 1;\n}\n");
        try
        {
            Assert.False(GscCompiledScriptDetector.IsCompiledFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCompiledFile_EmptyFile_ReturnsFalse()
    {
        var path = WriteTempFile([]);
        try
        {
            Assert.False(GscCompiledScriptDetector.IsCompiledFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCompiledFile_MissingFile_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gsc");
        Assert.False(GscCompiledScriptDetector.IsCompiledFile(path));
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gsc");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
