using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using GSCLSP.Core.Indexing;
using GSCLSP.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Benchmark;

[MemoryDiagnoser]
public class GscDefinitionHandlerBenchmark
{
    private GscIndexer _indexer = null!;
    private GscDefinitionHandler _definitionHandler = null!;
    private string _testFilePath = null!;
    private DefinitionParams _definitionRequest = null!;
    [GlobalSetup]
    public void Setup()
    {
        _indexer = new GscIndexer();
        _definitionHandler = new GscDefinitionHandler(_indexer, null);
        // Create test workspace
        string tempDir = Path.Combine(Path.GetTempPath(), "gsclsp-definition-" + Guid.NewGuid());
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
        // Create definition request pointing to a function call
        _definitionRequest = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = new Uri(_testFilePath)
            },
            Position = new Position(8, 15)
        };
    }

    [Benchmark]
    public async Task DefinitionHandler_HandleAsync()
    {
        await _definitionHandler.Handle(_definitionRequest, CancellationToken.None);
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