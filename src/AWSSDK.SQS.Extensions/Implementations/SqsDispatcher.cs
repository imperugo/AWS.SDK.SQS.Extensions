using Amazon.SQS;
using Amazon.SQS.Model;

using AWSSDK.SQS.Extensions.Abstractions;
using AWSSDK.SQS.Extensions.Extensions;

using Microsoft.Extensions.Logging;

namespace AWSSDK.SQS.Extensions.Implementations;

internal sealed class SqsDispatcher : ISqsDispatcher
{
    private readonly ISqsQueueHelper sqsHelper;
    private readonly ILogger<SqsDispatcher> logger;
    private readonly IMessageSerializer messageSerializer;

    public IAmazonSQS SqsClient { get; }

    public SqsDispatcher(
        IAmazonSQS sqsService,
        ISqsQueueHelper sqsHelper,
        ILogger<SqsDispatcher> logger,
        IMessageSerializer messageSerializer)
    {
        SqsClient = sqsService ?? throw new ArgumentNullException(nameof(sqsService));

        this.sqsHelper = sqsHelper;
        this.logger = logger;
        this.messageSerializer = messageSerializer;
    }

    public Task QueueAsync<T>(T obj, string queueName, CancellationToken cancellationToken = default)
    {
        return QueueAsync(obj, queueName, 0, cancellationToken);
    }

    public Task QueueAsync<T>(T obj, string queueName, int delaySeconds, CancellationToken cancellationToken = default)
    {
        return QueueAsync(messageSerializer.Serialize(obj), queueName, delaySeconds, cancellationToken);
    }

    public Task QueueAsync<T>(T[] obj, string queueName, CancellationToken cancellationToken = default)
    {
        return QueueAsync(obj, queueName, 0, cancellationToken);
    }

    public Task QueueAsync<T>(T[] obj, string queueName, int delaySeconds, CancellationToken cancellationToken = default)
    {
        var serializedObects = new string[obj.Length];

        for (var i = 0; i < obj.Length; i++)
            serializedObects[i] = messageSerializer.Serialize(obj);

        return QueueAsync(serializedObects, queueName, delaySeconds, cancellationToken);
    }

    public Task QueueAsync(string serializedObject, string queueName, CancellationToken cancellationToken = default)
    {
        return QueueAsync(serializedObject, queueName, 0, cancellationToken);
    }

    public Task QueueAsync(string serializedObject, string queueName, int delaySeconds, CancellationToken cancellationToken = default)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = sqsHelper.GetQueueName(queueName),
            MessageBody = serializedObject,
            DelaySeconds = delaySeconds
        };

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Pushing message into SQS Queue: {QueueName} with this value {SerializedValue}", queueName, serializedObject);

        return SqsClient.SendMessageAsync(request, cancellationToken);
    }

    public Task QueueAsync(string[] serializedObject, string queueName, int delaySeconds, CancellationToken cancellationToken = default)
    {
        var requests = new SendMessageRequest[serializedObject.Length];

        for (var i = 0; i < serializedObject.Length; i++)
        {
            requests[i] = new SendMessageRequest
            {
                QueueUrl = sqsHelper.GetQueueName(queueName),
                MessageBody = serializedObject[i],
                DelaySeconds = delaySeconds
            };
        }

        return QueueAsync(requests, cancellationToken);
    }

    public Task QueueAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        // This is needed in case the caller will add the queue name instead of queue url
        request.QueueUrl = sqsHelper.GetQueueName(request.QueueUrl);

        return SqsClient.SendMessageAsync(request, cancellationToken);
    }

    public async Task QueueAsync(SendMessageRequest[] requests, CancellationToken cancellationToken = default)
    {
        // 10 è il numero massimo di messaggi che posso inviare a SQS in una singola richiesta
        const int maxNumberOfMessages = 10;

        // Li gruppo per coda
        foreach (var group in requests.GroupBy(x => x.QueueUrl))
        {
            await Parallel.ForEachAsync(group.ToList().Split(maxNumberOfMessages), cancellationToken, async (messages, token) =>
            {
                var queueUrl = sqsHelper.GetQueueName(messages[0].QueueUrl);
                var entries = new List<SendMessageBatchRequestEntry>(maxNumberOfMessages);

                foreach (var message in messages)
                {
                    var entry = new SendMessageBatchRequestEntry();
                    entry.Id = Guid.NewGuid().ToString("N");
                    entry.MessageBody = message.MessageBody;
                    entry.DelaySeconds = message.DelaySeconds;
                    entries.Add(entry);
                }

                await SqsClient.SendMessageBatchAsync(queueUrl, entries, token).ConfigureAwait(false);
            })
                .ConfigureAwait(false);
        }
    }
}
