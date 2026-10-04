using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Process isolation for signature validation: the worker crashes, hangs, over-allocates or lies,
/// and the gateway answers a named 503 while staying alive to serve the next request.
/// <para>
/// The crash proof uses a <c>/Kids</c> element that is a bare-reference cycle
/// (<c>4 0 obj 5 0 R endobj</c> / <c>5 0 obj 4 0 R endobj</c>). PdfPig resolves every kid through
/// the unguarded <c>DirectObjectFinder.TryGet</c> self-recursion (verified by decompiling 0.1.16:
/// the catalog path uses the guarded <c>Get</c>, the kids path does not), so with the pre-check
/// bypassed the worker dies with a real, uncatchable stack overflow (measured exit
/// <c>0xC00000FD</c>). A cycle of dictionaries does NOT crash — the page walk is iterative with a
/// visited window — so that shape cannot prove isolation and is not used here.
/// </para>
/// </summary>
public sealed class SignatureWorkerIsolationTests : IDisposable
{
    private readonly SignatureFixtures _fixtures = new();

    static SignatureWorkerIsolationTests() =>
        Environment.SetEnvironmentVariable("ODVGATEWAY_SIGNATURE_WORKER_TEST", "1");

    [Fact]
    public async Task WorkerCrash_BareRefCycleWithBypassedPrecheck_ParentSurvivesWithNamedFailure()
    {
        var bytes = KidsBareRefCyclePdf();

        // The same bytes are caught by the pre-check: the bypass is what exposes the crash, so a
        // worker that answered anything but "crashed" would disprove the fixture, not the isolation.
        var precheck = Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        Assert.Contains(SignatureValidationReasons.PageTreeCyclic, precheck.Message, StringComparison.Ordinal);

        using var client = CreateClient();
        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() => client.ValidateAsync(
            CreateOptions(testMode: SignatureWorkerProtocol.TestModes.BypassPrecheck),
            bytes, gatewayHost: null, TestContext.Current.CancellationToken));
        Assert.Equal(SignatureValidationReasons.ValidationWorkerCrashed, exception.Code);
        Assert.DoesNotContain("stack", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The parent process is alive and well: a healthy file validates in-process right after.
        var healthy = await CreateService().ValidateAsync(
            File.ReadAllBytes(_fixtures.CreateUnsignedPdf()), TestContext.Current.CancellationToken);
        Assert.Empty(healthy.Signatures);
    }

    [Fact]
    public async Task WorkerHang_ExceedingTimeout_YieldsNamedTimeout()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() => client.ValidateAsync(
            CreateOptions(testMode: SignatureWorkerProtocol.TestModes.Hang, workerTimeoutSeconds: 2),
            bytes, gatewayHost: null, TestContext.Current.CancellationToken));

        Assert.Equal(SignatureValidationReasons.ValidationWorkerTimeout, exception.Code);
    }

    [Fact]
    public async Task WorkerAllocate_ExceedingMemoryCap_YieldsNamedMemory()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() => client.ValidateAsync(
            CreateOptions(
                testMode: SignatureWorkerProtocol.TestModes.Allocate,
                workerMaxMemoryBytes: 128L * 1024L * 1024L),
            bytes, gatewayHost: null, TestContext.Current.CancellationToken));

        Assert.Equal(SignatureValidationReasons.ValidationWorkerMemory, exception.Code);
    }

    [Fact]
    public async Task WorkerBadOutput_YieldsNamedCrash()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() => client.ValidateAsync(
            CreateOptions(testMode: SignatureWorkerProtocol.TestModes.BadOutput),
            bytes, gatewayHost: null, TestContext.Current.CancellationToken));

        Assert.Equal(SignatureValidationReasons.ValidationWorkerCrashed, exception.Code);
    }

    [Fact]
    public async Task WorkerHealthyDocument_ReturnsSameVerdictAsInProcess()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var bytes = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));

        using var client = CreateClient();
        var worker = await client.ValidateAsync(
            CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
        var direct = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        var throughWorker = Assert.Single(worker.Signatures);
        var inProcess = Assert.Single(direct.Signatures);
        Assert.Equal(inProcess.FieldName, throughWorker.FieldName);
        Assert.Equal(inProcess.SubFilter, throughWorker.SubFilter);
        Assert.Equal(inProcess.Kind, throughWorker.Kind);
        Assert.Equal(inProcess.Integrity, throughWorker.Integrity);
        Assert.Equal(inProcess.IntegrityReason, throughWorker.IntegrityReason);
        Assert.Equal(inProcess.CoversWholeFile, throughWorker.CoversWholeFile);
        Assert.Equal(inProcess.Trust, throughWorker.Trust);
        Assert.Equal(inProcess.TrustReason, throughWorker.TrustReason);
        Assert.Equal(inProcess.Signer, throughWorker.Signer);
        Assert.Equal(inProcess.SigningTimeSource, throughWorker.SigningTimeSource);
        Assert.Equal(direct.Diagnostics, worker.Diagnostics);
    }

    [Fact]
    public async Task WorkerValidation_NeverWritesDocumentBytesToDisk()
    {
        // Every worker of this client is pointed at a private temp directory it never needs, so
        // any entry in it after a run means document bytes touched the disk. The shared %TEMP% is
        // deliberately never enumerated: it routinely holds hundreds of thousands of entries.
        using var client = CreateClient();
        Assert.NotNull(client.IsolatedTempRoot);
        var bytes = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));

        var response = await client.ValidateAsync(
            CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
        Assert.Single(response.Signatures);

        Assert.Empty(Directory.EnumerateFileSystemEntries(
            client.IsolatedTempRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PooledWorkers_AreReusedAcrossRequests()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        for (var i = 0; i < 3; i++)
        {
            var response = await client.ValidateAsync(
                CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
            Assert.Empty(response.Signatures);
        }

        Assert.Equal(1, client.SpawnedWorkerCount);
        Assert.Equal(1, client.IdleWorkerCount);
    }

    [Fact]
    public async Task PooledFormatError_KeepsWorkerForReuse()
    {
        // A document outcome (as opposed to a worker failure) retires nothing: the worker that
        // reported it serves the next request.
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateNonPdf("junk.pdf", 4096));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<PdfSignatureFormatException>(() => client.ValidateAsync(
                CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, client.SpawnedWorkerCount);
        Assert.Equal(1, client.IdleWorkerCount);
    }

    [Fact]
    public async Task PooledWorker_IsRecycledAfterMaxRequests()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        for (var i = 0; i < SignatureWorkerProtocol.MaxRequestsPerWorker + 1; i++)
        {
            var response = await client.ValidateAsync(
                CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
            Assert.Empty(response.Signatures);
        }

        Assert.Equal(2, client.SpawnedWorkerCount);
        Assert.Equal(1, client.IdleWorkerCount);
    }

    [Fact]
    public async Task PooledWorkerTimeout_DestroysWorkerWithoutReuse()
    {
        using var client = CreateClient();
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());

        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() =>
            client.ValidatePooledForTestingAsync(
                CreateOptions(testMode: SignatureWorkerProtocol.TestModes.Hang, workerTimeoutSeconds: 2),
                bytes, gatewayHost: null, TestContext.Current.CancellationToken));
        Assert.Equal(SignatureValidationReasons.ValidationWorkerTimeout, exception.Code);
        Assert.Equal(1, client.SpawnedWorkerCount);
        Assert.Equal(0, client.IdleWorkerCount);

        // The next request spawns a fresh worker and succeeds: the hung one was destroyed.
        var response = await client.ValidateAsync(
            CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
        Assert.Empty(response.Signatures);
        Assert.Equal(2, client.SpawnedWorkerCount);
    }

    [Fact]
    public async Task PooledWorkerCrash_DestroysWorkerWithoutReuse()
    {
        using var client = CreateClient();

        var exception = await Assert.ThrowsAsync<SignatureWorkerException>(() =>
            client.ValidatePooledForTestingAsync(
                CreateOptions(testMode: SignatureWorkerProtocol.TestModes.BypassPrecheck),
                KidsBareRefCyclePdf(), gatewayHost: null, TestContext.Current.CancellationToken));
        Assert.Equal(SignatureValidationReasons.ValidationWorkerCrashed, exception.Code);
        Assert.Equal(1, client.SpawnedWorkerCount);
        Assert.Equal(0, client.IdleWorkerCount);

        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());
        var response = await client.ValidateAsync(
            CreateOptions(), bytes, gatewayHost: null, TestContext.Current.CancellationToken);
        Assert.Empty(response.Signatures);
        Assert.Equal(2, client.SpawnedWorkerCount);
    }

    [Fact]
    public async Task Endpoint_WorkerCrash_ReturnsNamed503AndKeepsServing()
    {
        var cyclicPath = Path.Combine(_fixtures.FileDirectory, "cyclic-kids.pdf");
        File.WriteAllBytes(cyclicPath, KidsBareRefCyclePdf());
        using var factory = new WorkerFactory(
            _fixtures, SignatureWorkerProtocol.TestModes.BypassPrecheck);
        using var client = factory.CreateClient();
        var cyclicSession = PrepareSession(factory, cyclicPath);

        using (var response = await client.GetAsync(
            $"/signatures/{cyclicSession}/0", TestContext.Current.CancellationToken))
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("\"code\":\"validation-worker-crashed\"", body);
            Assert.DoesNotContain("TryGet", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Stack", body, StringComparison.OrdinalIgnoreCase);
        }

        // The gateway process survived the worker's stack overflow: the next request is served.
        var healthySession = PrepareSession(factory, _fixtures.CreateUnsignedPdf());
        using var healthy = await client.GetAsync(
            $"/signatures/{healthySession}/0", TestContext.Current.CancellationToken);
        var healthyBody = await healthy.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Contains("\"signatures\":[]", healthyBody);
    }

    [Fact]
    public async Task Endpoint_WorkerHang_ReturnsNamedTimeout503()
    {
        using var factory = new WorkerFactory(
            _fixtures, SignatureWorkerProtocol.TestModes.Hang, timeoutSeconds: 2);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, _fixtures.CreateUnsignedPdf());

        using var response = await client.GetAsync(
            $"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"code\":\"validation-worker-timeout\"", body);
    }

    [Fact]
    public async Task Endpoint_WorkerAllocate_ReturnsNamedMemory503()
    {
        using var factory = new WorkerFactory(
            _fixtures, SignatureWorkerProtocol.TestModes.Allocate, maxMemoryBytes: 128L * 1024L * 1024L);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, _fixtures.CreateUnsignedPdf());

        using var response = await client.GetAsync(
            $"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"code\":\"validation-worker-memory\"", body);
    }

    /// <summary>
    /// A classic-xref file whose <c>/Pages /Kids</c> element is a bare-reference cycle. The gateway's
    /// own pre-check proves it cyclic; PdfPig, reached with the pre-check bypassed, overflows its
    /// stack resolving the kid through unguarded <c>TryGet</c> recursion.
    /// </summary>
    private static byte[] KidsBareRefCyclePdf() => WriteGraph(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
        "5 0 R",
        "4 0 R"
    ]);

    private static byte[] WriteGraph(List<string> objects)
    {
        using var stream = new MemoryStream();
        stream.Write("%PDF-1.7\n"u8);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            stream.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }

        var xref = stream.Position;
        stream.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            stream.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
        }

        stream.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return stream.ToArray();
    }

    private SignatureWorkerClient CreateClient() =>
        new(NullLogger.Instance, _fixtures.TempRoot);

    private SignatureValidationOptions CreateOptions(
        string? testMode = null,
        int workerTimeoutSeconds = 30,
        long workerMaxMemoryBytes = 512L * 1024L * 1024L) =>
        new()
        {
            Enabled = true,
            UseWindowsTrustedRoots = false,
            ExtraAnchorsDirectory = _fixtures.AnchorDirectory,
            CrlDirectory = _fixtures.CrlDirectory,
            RevocationMode = SignatureRevocationMode.Online,
            RevocationTimeoutSeconds = 1,
            WorkerTimeoutSeconds = workerTimeoutSeconds,
            WorkerMaxMemoryBytes = workerMaxMemoryBytes,
            WorkerTestMode = testMode
        };

    private PdfSignatureValidationService CreateService()
    {
        var options = CreateOptions();
        return new PdfSignatureValidationService(
            options,
            new TrustAnchorStore(options, NullLogger.Instance, _fixtures.TempRoot),
            new OfflineRevocationStore(options.CrlDirectory, NullLogger.Instance, _fixtures.TempRoot),
            NullLogger.Instance);
    }

    private static string PrepareSession(WebApplicationFactory<Program> factory, string filePath)
    {
        var sessions = factory.Services.GetRequiredService<GatewaySessionStore>();
        var stored = sessions.Store(new WebClientPrepRequest
        {
            UserId = "test-user",
            SessionId = "test-session",
            PortableDocuments =
            [
                new WebClientPortableDocument
                {
                    DocumentId = "document-1",
                    FileData = [$"file-1|pdf|{filePath}"]
                }
            ]
        });
        return stored.Session!.SessionKey;
    }

    public void Dispose() => _fixtures.Dispose();

    private sealed class WorkerFactory : WebApplicationFactory<Program>
    {
        private readonly SignatureFixtures _fixtures;
        private readonly string? _testMode;
        private readonly int? _timeoutSeconds;
        private readonly long? _maxMemoryBytes;

        public WorkerFactory(
            SignatureFixtures fixtures,
            string? testMode,
            int? timeoutSeconds = null,
            long? maxMemoryBytes = null)
        {
            _fixtures = fixtures;
            _testMode = testMode;
            _timeoutSeconds = timeoutSeconds;
            _maxMemoryBytes = maxMemoryBytes;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            // Unset worker knobs are omitted (not nulled): a null in-memory value binds to 0 for
            // numbers, which would clamp the memory cap to its 32 MiB floor and fail every test.
            var settings = new Dictionary<string, string?>
            {
                ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                ["ODVGateway:OpenDocViewerDistPath"] = string.Empty,
                ["ODVGateway:TrustClientFilePath"] = "true",
                ["ODVGateway:TrustedSourceRoots:0"] = _fixtures.FileDirectory,
                ["ODVGateway:signatures:enabled"] = "true",
                ["ODVGateway:signatures:isolateProcess"] = "true",
                ["ODVGateway:signatures:maxConcurrentValidations"] = "2",
                ["ODVGateway:signatures:useWindowsTrustedRoots"] = "false",
                ["ODVGateway:signatures:extraAnchorsDirectory"] = _fixtures.AnchorDirectory,
                ["ODVGateway:signatures:crlDirectory"] = _fixtures.CrlDirectory,
                ["ODVGateway:signatures:revocationMode"] = "Online",
                ["ODVGateway:signatures:revocationTimeoutSeconds"] = "1"
            };
            if (_timeoutSeconds is not null)
            {
                settings["ODVGateway:signatures:workerTimeoutSeconds"] = _timeoutSeconds.ToString();
            }

            if (_maxMemoryBytes is not null)
            {
                settings["ODVGateway:signatures:workerMaxMemoryBytes"] = _maxMemoryBytes.ToString();
            }

            if (_testMode is not null)
            {
                settings["ODVGateway:signatures:workerTestMode"] = _testMode;
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
        }
    }
}
