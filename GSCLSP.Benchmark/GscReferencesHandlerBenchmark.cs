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
public class GscReferencesHandlerBenchmark
{
    private GscIndexer _indexer = null!;
    private GscReferencesHandler _referencesHandler = null!;
    private string _testFilePath = null!;
    private ReferenceParams _referenceRequest = null!;
    [GlobalSetup]
    public void Setup()
    {
        _indexer = new GscIndexer();
        _referencesHandler = new GscReferencesHandler(_indexer, null);
        // Create test workspace
        string tempDir = Path.Combine(Path.GetTempPath(), "gsclsp-references-" + Guid.NewGuid());
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
        // Create test request - search for "another_func" references
        _referenceRequest = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = new Uri(_testFilePath)
            },
            Position = new Position(8, 5),
            Context = new ReferenceContext
            {
                IncludeDeclaration = true
            }
        };
    }

    [Benchmark]
    public async Task ReferencesHandler_HandleAsync()
    {
        await _referencesHandler.Handle(_referenceRequest, CancellationToken.None);
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