using Grpc.Core;
using Grpc.Net.Client;
using Pad.Common;
using Pad.Common.Grpc;

namespace Pad.Integration.Tests;

/// <summary>
/// Un receiver minimal, scris special pentru teste.
///
/// Vorbeste acelasi protocol ca receiver-ul adevarat (Subscribe, din broker.proto),
/// dar NU afiseaza meniu si NU trimite ACK singur. Asa putem controla exact momentul
/// in care confirmam un mesaj - sau ne deconectam fara sa-l confirmam deloc.
/// </summary>
public sealed class TestReceiver : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly AsyncDuplexStreamingCall<ReceiverEvent, ServerEvent> _call;

    /// <summary>Numele unic primit de la broker (ex. "ana-1").</summary>
    public string AssignedName { get; }

    private TestReceiver(GrpcChannel channel,
                         AsyncDuplexStreamingCall<ReceiverEvent, ServerEvent> call,
                         string assignedName)
    {
        _channel = channel;
        _call = call;
        AssignedName = assignedName;
    }

    /// <summary>Se conecteaza, se inregistreaza si asteapta numele unic de la broker.</summary>
    public static async Task<TestReceiver> ConnectAsync(string address, string name, params string[] subscribeTo)
    {
        var channel = GrpcChannel.ForAddress(address);
        var client = new BrokerService.BrokerServiceClient(channel);
        var call = client.Subscribe();

        var register = new RegisterReceiverRequest { Name = name };
        register.SubscribeTo.AddRange(subscribeTo);
        await call.RequestStream.WriteAsync(new ReceiverEvent { Register = register });

        // Primul raspuns trebuie sa fie confirmarea inregistrarii, cu numele atribuit.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!await call.ResponseStream.MoveNext(cts.Token)
            || call.ResponseStream.Current.EventCase != ServerEvent.EventOneofCase.Registered)
        {
            throw new InvalidOperationException("Broker-ul nu a confirmat inregistrarea receiver-ului.");
        }

        return new TestReceiver(channel, call, call.ResponseStream.Current.Registered.AssignedName);
    }

    /// <summary>
    /// Asteapta urmatorul mesaj. Returneaza null daca nu vine niciunul in timpul dat
    /// (asa verificam si cazul "NU trebuie sa mai primeasca nimic").
    /// </summary>
    public async Task<BrokerMessage?> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await _call.ResponseStream.MoveNext(cts.Token))
            {
                ServerEvent received = _call.ResponseStream.Current;
                if (received.EventCase == ServerEvent.EventOneofCase.Message)
                    return received.Message;
            }
        }
        catch (OperationCanceledException) { /* a expirat timpul: nu a venit nimic */ }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { /* idem */ }

        return null;
    }

    /// <summary>Confirma un mesaj catre broker.</summary>
    public Task AckAsync(string messageId)
    {
        return _call.RequestStream.WriteAsync(new ReceiverEvent
        {
            Ack = new ReceiverAck { MessageId = messageId, Status = Constants.StatusAck },
        });
    }

    /// <summary>Inchide conexiunea brusc, exact ca un receiver care "moare". </summary>
    public async ValueTask DisposeAsync()
    {
        _call.Dispose();
        await _channel.ShutdownAsync();
        _channel.Dispose();
    }
}
