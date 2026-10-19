using Pad.Common;
using Pad.Common.Grpc;
using Pad.Sender;
using Xunit;

namespace Pad.Integration.Tests;

/// <summary>
/// Teste de integrare: pornesc un broker gRPC adevarat si folosesc sender-ul REAL
/// (clasa Pad.Sender.BrokerClient), nu o imitatie. Receiver-ul este unul de test,
/// ca sa putem alege cand confirmam si cand ne deconectam.
///
/// Fiecare test isi are propriul port si propriul fisier WAL, deci nu se incurca intre ele.
/// </summary>
public class BrokerFlowTests : IDisposable
{
    private static readonly TimeSpan WaitForMessage = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitForNothing = TimeSpan.FromSeconds(4);

    // Un folder separat pentru fisierele WAL ale acestui test, sters la final.
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "pad-tests", Guid.NewGuid().ToString("N"));

    public BrokerFlowTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { /* nu e critic */ }
        GC.SuppressFinalize(this);
    }

    private string Wal(string name) => Path.Combine(_workDir, name + ".wal");

    /// <summary>Sender-ul adevarat, legat la brokerele date.</summary>
    private static BrokerClient NewSender(params string[] addresses) =>
        new(addresses, "sender", name => new Logger("test-" + name, toConsole: false));

    /// <summary>
    /// Asteapta pana cand broker-ul nu mai raporteaza receiver-ul ca fiind conectat.
    ///
    /// Broker-ul scoate receiver-ul din lista abia in blocul finally al lui Subscribe,
    /// adica ceva mai tarziu decat momentul in care clientul inchide conexiunea.
    /// Daca ne-am reconecta imediat, numele "ana-1" ar parea inca ocupat si am primi
    /// "ana-2" - alt destinatar, deci mesajul neconfirmat nu ne-ar mai reveni.
    /// </summary>
    private static async Task WaitUntilDisconnected(BrokerClient sender, string receiverName)
    {
        for (int i = 0; i < 100; i++)
        {
            if (!sender.ListReceivers().Contains(receiverName))
                return;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Broker-ul inca il vede pe {receiverName} conectat.");
    }

    // -----------------------------------------------------------------------
    // 1. Fluxul complet: sender -> broker -> receiver -> ACK
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FluxComplet_MesajulAjungeLaReceiverSiEsteConfirmat()
    {
        await using var broker = await TestBroker.StartAsync(Wal("flux"), "broker-flux");
        await using var receiver = await TestReceiver.ConnectAsync(broker.Address, "ana", "Anunt");

        using BrokerClient sender = NewSender(broker.Address);
        sender.ConnectWithWait();

        // Numele vine de la broker, deci sender-ul a fost inregistrat cu succes.
        Assert.Equal("sender-1", sender.Name);
        Assert.Equal("ana-1", receiver.AssignedName);

        BrokerMessage trimis = MessageFactory.FromText("Anunt", "Maine nu avem laborator");

        Assert.True(sender.SendWithRetry(trimis), "broker-ul nu a dat ACK sender-ului");

        BrokerMessage? primit = await receiver.ReceiveAsync(WaitForMessage);

        Assert.NotNull(primit);
        Assert.Equal(trimis.MessageId, primit!.MessageId);
        Assert.Equal(trimis.CorrelationId, primit.CorrelationId);
        Assert.Equal("Anunt", primit.MessageType);
        Assert.Contains("Maine nu avem laborator", primit.PayloadJson);

        await receiver.AckAsync(primit.MessageId);
    }

    // -----------------------------------------------------------------------
    // 2. Receiver-ul se deconecteaza INAINTE de ACK -> mesajul se relivreaza
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ReceiverDeconectatInainteDeAck_MesajulEsteRelivrat()
    {
        await using var broker = await TestBroker.StartAsync(Wal("relivrare"), "broker-relivrare");

        using BrokerClient sender = NewSender(broker.Address);
        sender.ConnectWithWait();

        BrokerMessage trimis;

        // Prima conexiune: primeste mesajul, dar moare fara sa trimita ACK.
        var primul = await TestReceiver.ConnectAsync(broker.Address, "ana", "Anunt");
        try
        {
            Assert.Equal("ana-1", primul.AssignedName);

            trimis = MessageFactory.FromText("Anunt", "mesaj care trebuie relivrat");
            Assert.True(sender.SendWithRetry(trimis));

            BrokerMessage? primit = await primul.ReceiveAsync(WaitForMessage);
            Assert.NotNull(primit);
            Assert.Equal(trimis.MessageId, primit!.MessageId);
        }
        finally
        {
            await primul.DisposeAsync();   // cade fara ACK
        }

        // Ii dam broker-ului timp sa inregistreze deconectarea, ca numele sa se elibereze.
        await WaitUntilDisconnected(sender, "ana-1");

        // A doua conexiune, acelasi nume de baza: broker-ul ii da din nou "ana-1",
        // pentru ca numele s-a eliberat. Deci mesajul neconfirmat ii revine.
        await using var alDoilea = await TestReceiver.ConnectAsync(broker.Address, "ana", "Anunt");
        Assert.Equal("ana-1", alDoilea.AssignedName);

        BrokerMessage? relivrat = await alDoilea.ReceiveAsync(WaitForMessage);

        Assert.NotNull(relivrat);
        Assert.Equal(trimis.MessageId, relivrat!.MessageId);

        await alDoilea.AckAsync(relivrat.MessageId);
    }

    // -----------------------------------------------------------------------
    // 3. Broker-ul repornit recupereaza mesajele din WAL
    // -----------------------------------------------------------------------
    [Fact]
    public async Task BrokerRepornit_MesajeleSuntRecuperateDinWal()
    {
        string wal = Wal("recuperare");
        BrokerMessage trimis = MessageFactory.FromText("Anunt", "mesaj salvat pe disc");

        // Primul broker: primeste si salveaza mesajul, dar NU il livreaza
        // (pornit fara dispatcher), ca sa ramana nelivrat in WAL.
        await using (var primul = await TestBroker.StartAsync(wal, "broker-wal-1", startDispatcher: false))
        {
            Assert.Equal(0, primul.RecoveredCount);   // WAL gol la prima pornire

            using BrokerClient sender = NewSender(primul.Address);
            sender.ConnectWithWait();
            Assert.True(sender.SendWithRetry(trimis));
        }
        // aici primul broker s-a oprit si a eliberat fisierul WAL

        Assert.True(File.Exists(wal), "WAL-ul nu a fost scris pe disc");

        // Al doilea broker, acelasi WAL: trebuie sa recupereze mesajul si sa-l livreze.
        await using var alDoilea = await TestBroker.StartAsync(wal, "broker-wal-2");

        Assert.True(alDoilea.RecoveredCount >= 1,
                    $"asteptam cel putin un mesaj recuperat, am primit {alDoilea.RecoveredCount}");

        await using var receiver = await TestReceiver.ConnectAsync(alDoilea.Address, "ana", "Anunt");
        BrokerMessage? primit = await receiver.ReceiveAsync(WaitForMessage);

        Assert.NotNull(primit);
        Assert.Equal(trimis.MessageId, primit!.MessageId);

        await receiver.AckAsync(primit.MessageId);
    }

    // -----------------------------------------------------------------------
    // 4. Acelasi messageId trimis de doua ori -> receiver-ul il primeste O SINGURA data
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AcelasiMessageIdTrimisDeDouaOri_NuProduceDublura()
    {
        await using var broker = await TestBroker.StartAsync(Wal("duplicat"), "broker-duplicat");
        await using var receiver = await TestReceiver.ConnectAsync(broker.Address, "ana", "Anunt");

        using BrokerClient sender = NewSender(broker.Address);
        sender.ConnectWithWait();

        // Acelasi obiect trimis de doua ori = exact ce face retry-ul sender-ului
        // cand ACK-ul se pierde pe drum.
        BrokerMessage trimis = MessageFactory.FromText("Anunt", "mesaj trimis de doua ori");

        Assert.True(sender.SendWithRetry(trimis), "prima trimitere trebuie sa primeasca ACK");

        BrokerMessage? primul = await receiver.ReceiveAsync(WaitForMessage);
        Assert.NotNull(primul);
        Assert.Equal(trimis.MessageId, primul!.MessageId);
        await receiver.AckAsync(primul.MessageId);

        // A doua trimitere: broker-ul vede messageId-ul deja in WAL,
        // raspunde tot ACK, dar nu il mai pune in coada.
        Assert.True(sender.SendWithRetry(trimis), "retrimiterea trebuie sa primeasca tot ACK");

        BrokerMessage? alDoilea = await receiver.ReceiveAsync(WaitForNothing);

        Assert.Null(alDoilea);   // nicio dublura
    }
}
