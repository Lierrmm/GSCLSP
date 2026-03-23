using BenchmarkDotNet.Attributes;
using GSCLSP.Lexer;
using Microsoft.VSDiagnostics;

namespace GSCLSP.Benchmark;
[CPUUsageDiagnoser]
public class GscLexerBenchmark
{
    private GscLexer _lexer = null!;
    private string _source = null!;
    [GlobalSetup]
    public void Setup()
    {
        _lexer = new GscLexer();
        _source = @"
#include scripts\common;

main()
{
    self iprintlnbold(""hello "" + self.name);
    for(i = 0; i < 200; i++)
    {
        wait 0.05;
        if(i % 2 == 0)
        {
            self notify(""tick"");
        }
    }

    // line comment
    /* block comment */
    localValue = 0x1A + 42.5;
    foo::bar(localValue, self, level);
}
";
    }

    [Benchmark]
    public void Lexer_Lex()
    {
        _ = _lexer.Lex(_source);
    }
}