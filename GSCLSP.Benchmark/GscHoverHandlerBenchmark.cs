using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Microsoft.VSDiagnostics;

namespace GSCLSP.Benchmark;

[CPUUsageDiagnoser]
public class GscHoverHandlerBenchmark
{
    private GscIndexer _indexer = null!;
    private GscHoverHandler _hoverHandler = null!;
    private string _testFilePath = null!;
    private HoverParams _hoverRequest = null!;
    [GlobalSetup]
    public void Setup()
    {
        _indexer = new GscIndexer();
        _hoverHandler = new GscHoverHandler(_indexer);
        // Create test workspace
        string tempDir = Path.Combine(Path.GetTempPath(), "gsclsp-hover-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        CreateTestFile(Path.Combine(tempDir, "common.gsc"), @"
#include scripts\utility;

common_function(arg1, arg2) 
{
    // test function
}

another_func() 
{
    common_function(1, 2);
}
");
        CreateTestFile(Path.Combine(tempDir, "main.gsc"), @"
#include scripts\common;

main_function() 
{
    // calling
}

function_with_params(a, b, c) 
{
    another_func();
    common_function(10, 20);
}
");
        CreateTestFile(Path.Combine(tempDir, "utility.gsc"), @"
util_1() { }
util_2() { }
util_3() { }
");
        // Index workspace
        _indexer.IndexWorkspace(tempDir);
        _testFilePath = Path.Combine(tempDir, "main.gsc");
        // Create test request
        _hoverRequest = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = new Uri(_testFilePath)
            },
            Position = new Position(8, 10)
        };
    }

    [Benchmark]
    public async Task HoverHandler_HandleAsync()
    {
        await _hoverHandler.Handle(_hoverRequest, CancellationToken.None);
    }

    private static void CreateTestFile(string filePath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_testFilePath != null && File.Exists(_testFilePath))
        {
            var dir = Path.GetDirectoryName(_testFilePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}