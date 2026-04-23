using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using Microsoft.VSDiagnostics;

namespace GSCLSP.Benchmark;

[CPUUsageDiagnoser]
public class GscDiagnosticsHandlerBenchmark
{
    private GscIndexer _indexer = null!;
    private GscDiagnosticsHandler _diagnosticsHandler = null!;
    private string _testFilePath = null!;
    private string _testFileText = null!;
    private string _tempDir = null!;

    [GlobalSetup]
    public void Setup()
    {
        _indexer = new GscIndexer();

        _tempDir = Path.Combine(Path.GetTempPath(), "gsclsp-diagnostics-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        CreateTestFile(Path.Combine(_tempDir, "scripts", "utility.gsc"), @"
util_log(msg) { }
util_assert(cond) { }
util_wait(time) { wait(time); }
util_random_int(min, max) { return randomInt(max - min) + min; }
util_clamp(val, lo, hi) { if (val < lo) return lo; if (val > hi) return hi; return val; }
");

        CreateTestFile(Path.Combine(_tempDir, "scripts", "common.gsc"), @"
#include scripts\utility;

common_init()
{
    util_log(""init"");
}

common_cleanup()
{
    util_log(""cleanup"");
}

common_get_player(index)
{
    return level.players[index];
}

common_broadcast(msg)
{
    util_log(msg);
    iprintlnbold(msg);
}
");

        _testFileText = @"
#include scripts\common;

main()
{
    common_init();
    common_cleanup();
    local_helper();
    unknown_func_a();
    unknown_func_b(1, 2);
}

local_helper()
{
    common_get_player(0);
    util_assert(true);
    another_unknown();
    common_broadcast(""hello"");
}

run_loop(count)
{
    /* block comment with function names: fake_func() */
    for (i = 0; i < count; i++)
    {
        local_helper();
        common_get_player(i);
        missing_function(i);
        util_clamp(i, 0, 10);
    }
}

on_player_spawn(player)
{
    // single line comment: ignored_call()
    local_helper();
    unknown_spawn_func(player);
    scripts\common::common_get_player(0);
}
";

        CreateTestFile(Path.Combine(_tempDir, "scripts", "main.gsc"), _testFileText);

        _indexer.IndexWorkspace(_tempDir);
        _testFilePath = Path.Combine(_tempDir, "scripts", "main.gsc");

        _diagnosticsHandler = new GscDiagnosticsHandler(_indexer, null!, new GscDocumentStore());
    }

    [Benchmark]
    public async Task DiagnosticsHandler_CollectAsync()
    {
        _ = await _diagnosticsHandler.CollectDiagnosticsAsync(_testFilePath, _testFileText, CancellationToken.None);
    }

    private static void CreateTestFile(string filePath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_tempDir != null && Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
