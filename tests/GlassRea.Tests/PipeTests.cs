using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using GlassRea.Service;
using Caller = GlassRea.Service.PipeCallers.Caller;

namespace GlassRea.Tests;

public class PipeTests
{
    private const string Classic = @"C:\Program Files\Glass-Rea\App\GlassRea.exe";
    private const string Package = @"C:\Program Files\WindowsApps\TemRevil.Glass-Rea_1.2.9.0_x64__m8znxtvwyfwk8";

    [Fact]
    public void SetupsAppIsLetInOnlyFromItsInstallFolder()
    {
        Assert.True(PipeCallers.Allowed(new Caller(Classic, null, null, false), Classic));
        Assert.True(PipeCallers.Allowed(new Caller(Classic.ToUpperInvariant(), null, null, false), Classic));
        Assert.False(PipeCallers.Allowed(new Caller(@"C:\Users\someone\Downloads\GlassRea.exe", null, null, false), Classic));
        Assert.False(PipeCallers.Allowed(new Caller(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", null, null, false), Classic));
        Assert.False(PipeCallers.Allowed(new Caller(null, null, null, false), Classic));
        // The Store copy's service has no unpackaged app to let in.
        Assert.False(PipeCallers.Allowed(new Caller(Classic, null, null, false), classicApp: null));
    }

    [Fact]
    public void TheStoreAppIsLetInByItsPackageAndItsProgram()
    {
        var app = Path.Combine(Package, "GlassRea.exe");
        Assert.True(PipeCallers.Allowed(new Caller(app, Packaging.StoreFamily, Package, false), null));
        Assert.True(PipeCallers.Allowed(new Caller(app, Packaging.StoreFamily, Package, false), Classic));
        // Another program started inside the package, or the same file name in another package, isn't the app.
        Assert.False(PipeCallers.Allowed(new Caller(@"C:\Windows\System32\cmd.exe", Packaging.StoreFamily, Package, false), null));
        Assert.False(PipeCallers.Allowed(new Caller(Path.Combine(Package, "Service", "GlassRea.Service.exe"), Packaging.StoreFamily, Package, false), null));
        Assert.False(PipeCallers.Allowed(new Caller(@"C:\Other\GlassRea.exe", "Someone.Else_abcdefghjkmnp", @"C:\Other", false), Classic));
        Assert.False(PipeCallers.Allowed(new Caller(app, Packaging.StoreFamily, null, false), null));
    }

    [Fact]
    public void AnElevatedAdministratorIsLetIn()
    {
        Assert.True(PipeCallers.Allowed(new Caller(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", null, null, true), null));
        Assert.True(PipeCallers.Allowed(new Caller(null, null, null, true), Classic));
    }

    [Fact]
    public void ThisProcessIsDescribedAsWindowsSeesIt()
    {
        var me = PipeCallers.Describe(Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, me.Image, ignoreCase: true);
        Assert.Null(me.PackageFamily);
        Assert.Null(me.PackageFolder);
        Assert.Equal(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), me.ElevatedAdmin);
        Assert.True(PipeCallers.Allowed(me, Environment.ProcessPath));
    }

    [Fact]
    public void ThePipeIsOpenOnlyToPeopleAtThisPcAndOnlyTheServiceServesIt()
    {
        var rules = PipeServer.Security().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        PipeAccessRule Rule(WellKnownSidType who) => Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(who, null)));

        Assert.Equal(AccessControlType.Deny, Rule(WellKnownSidType.NetworkSid).AccessControlType);
        var people = Rule(WellKnownSidType.InteractiveSid);
        Assert.Equal(AccessControlType.Allow, people.AccessControlType);
        Assert.Equal(0, (int)(people.PipeAccessRights & PipeAccessRights.CreateNewInstance));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [Fact]
    public async Task TheAppHangsUpOnAPipeTheServiceDoesNotServe()
    {
        var name = "GlassRea-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var answered = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            if (await reader.ReadLineAsync() is null) return;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Response(true), Pipe.Json));
        });

        using var client = new PipeClient(name);
        var e = await Assert.ThrowsAsync<NotTheServiceException>(() => client.Call<object>(new Request("status")));
        Assert.Equal(PipeClient.NotTheService, e.Message);
    }

    [Fact]
    public async Task ARefusedProgramIsToldSoInsteadOfBothSidesWaiting()
    {
        // The service's pipe has no buffer: a write waits until the other side reads.
        var name = "GlassRea-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0);
        var served = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await PipeServer.Refuse(reader, writer, CancellationToken.None);
        });

        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        var answer = Task.Run(async () =>
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Request("status"), Pipe.Json));
            return await reader.ReadLineAsync();
        });

        Assert.Same(answer, await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(5))));
        var response = JsonSerializer.Deserialize<Response>((await answer)!, Pipe.Json)!;
        Assert.False(response.Ok);
        Assert.Equal(PipeServer.Refusal, response.Error);
        await served.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AServiceThatNeverAnswersTimesOutOnceInsteadOfHoldingTheAppForever()
    {
        var name = "GlassRea-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var requests = 0;
        _ = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
            while (await reader.ReadLineAsync() is not null) Interlocked.Increment(ref requests); // and never answers
        });

        using var client = new PipeClient(name) { Trusts = _ => true, Answer = TimeSpan.FromSeconds(1) };
        var call = client.Call<object>(new Request("status"));
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.Equal(1, requests); // not sent again: it may have been a change, and waiting twice is twice as long
    }

    [Fact]
    public async Task ClosingAfterTheServiceHungUpClosesEverything()
    {
        var name = "GlassRea-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        await accepted;
        var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        await server.DisposeAsync(); // the service turned the app away, or restarted

        PipeClient.Close(reader, writer, pipe);
        Assert.False(pipe.IsConnected);
        Assert.Throws<ObjectDisposedException>(() => pipe.ReadByte());
    }

    [Fact]
    public void TheStoreFamilyIsTheOneWindowsGivesTheManifestsIdentity()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "GlassRea.slnx"))) dir = dir.Parent;
        var manifest = XDocument.Load(Path.Combine(dir.FullName, "src", "GlassRea.App", "Store", "Package.appxmanifest"));
        var identity = manifest.Descendants().First(e => e.Name.LocalName == "Identity");

        var family = $"{identity.Attribute("Name")!.Value}_{PublisherId(identity.Attribute("Publisher")!.Value)}";
        Assert.Equal(Packaging.StoreFamily, family);
        // Checked against a family name everyone knows.
        Assert.Equal("8wekyb3d8bbwe", PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    /// <summary>Windows' publisher id: the first 64 bits of the publisher's SHA-256, 13 characters of 5 bits each.</summary>
    private static string PublisherId(string publisher)
    {
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var bits = BitConverter.ToUInt64(SHA256.HashData(Encoding.Unicode.GetBytes(publisher)).AsSpan(0, 8).ToArray().Reverse().ToArray());
        var id = new StringBuilder();
        for (var i = 0; i < 13; i++) id.Append(Alphabet[(int)((i < 12 ? bits >> (59 - i * 5) : bits << 1) & 31)]);
        return id.ToString();
    }
}
