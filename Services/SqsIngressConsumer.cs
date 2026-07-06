using Amazon.SQS;
using Amazon.SQS.Model;
using EventForge.Configuration;
using EventForge.Models;
using EventForge.Storage;
using EventForge.WebSocket;
using Microsoft.Extensions.Options;

namespace EventForge.Services;

/// <summary>Long-poll fq-events-bus-ingress; persist + WebSocket fanout by manifest tenant_id.</summary>
public sealed class SqsIngressConsumer : BackgroundService
{
    private readonly EventForgeOptions _opts;
    private readonly IEventStore _events;
    private readonly WsConnectionManager _ws;
    private readonly ILogger<SqsIngressConsumer> _log;
    private readonly IAmazonSQS? _sqs;
    private readonly string? _queueUrl;

    public SqsIngressConsumer(
        IOptions<EventForgeOptions> options,
        IEventStore events,
        WsConnectionManager ws,
        ILogger<SqsIngressConsumer> log)
    {
        _opts = options.Value;
        _events = events;
        _ws = ws;
        _log = log;
        _queueUrl = ResolveQueueUrl(_opts);
        if (!string.IsNullOrWhiteSpace(_queueUrl))
            _sqs = new AmazonSQSClient(Amazon.RegionEndpoint.GetBySystemName(_opts.AwsRegion));
    }

    static string? ResolveQueueUrl(EventForgeOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.IngressQueueUrl))
            return opts.IngressQueueUrl.Trim();
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sqs == null || string.IsNullOrWhiteSpace(_queueUrl))
        {
            _log.LogInformation("SQS ingress consumer disabled (IngressQueueUrl not set)");
            return;
        }

        _log.LogInformation("SQS ingress consumer started queue={Queue}", _queueUrl);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resp = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                    VisibilityTimeout = 120,
                }, stoppingToken);

                foreach (var msg in resp.Messages ?? [])
                    await HandleMessageAsync(msg, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SQS ingress receive failed");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    async Task HandleMessageAsync(Message msg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(msg.Body) || string.IsNullOrWhiteSpace(msg.ReceiptHandle))
            return;

        if (!ManifestParser.TryParse(msg.Body, out var parsed, out var skipReason))
        {
            _log.LogDebug("Ingress skip message={Reason}", skipReason);
            await DeleteAsync(msg.ReceiptHandle, ct);
            return;
        }

        var record = await _events.PersistAsync(
            parsed!.AppId,
            parsed.JobId,
            parsed.EventType,
            parsed.ManifestJson,
            parsed.CompletedAt,
            parsed.Error,
            ct);

        if (record != null)
        {
            var serverMsg = ManifestParser.ToServerMessage(record);
            await _ws.BroadcastAsync(parsed.AppId, serverMsg, parsed.EventType, ct);
            _log.LogInformation(
                "Ingress event {Type} job={Job} app={App}",
                parsed.EventType, parsed.JobId[..Math.Min(8, parsed.JobId.Length)], parsed.AppId);
        }

        await DeleteAsync(msg.ReceiptHandle, ct);
    }

    async Task DeleteAsync(string receiptHandle, CancellationToken ct)
    {
        if (_sqs == null || string.IsNullOrWhiteSpace(_queueUrl)) return;
        try
        {
            await _sqs.DeleteMessageAsync(_queueUrl, receiptHandle, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ingress delete failed");
        }
    }

    public override void Dispose()
    {
        _sqs?.Dispose();
        base.Dispose();
    }
}
