using Amazon.SQS;
using Amazon.SQS.Model;

using SQS.Extensions.Extensions;

using Microsoft.Extensions.Logging;

using SQS.Extensions.Abstractions;

namespace SQS.Extensions.Implementations;

/// <summary>
/// Jil implementation of <see cref="ISqsDispatcher"/>
/// </summary>
internal sealed class SqsDispatcher : ISqsDispatcher
{
    private readonly ISqsQueueHelper sqsHelper;
    private readonly ILogger<SqsDispatcher> logger;
    private readonly IMessageSerializer messageSerializer;

    public IAmazonSQS SqsClient { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqsDispatcher"/> class.
    /// </summary>
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

    /// <inheritdoc/>
    public Task QueueAsync<T>(T obj, string queueName, int delaySeconds = 0, CancellationToken cancellationToken = default)
    {
        return QueueAsync(messageSerializer.Serialize(obj), queueName, delaySeconds, cancellationToken);
    }

    /// <inheritdoc/>
    public Task QueueBatchAsync<T>(IList<T> obj, string queueName, int delaySeconds = 0, int maxNumberOfMessagesForBatch = 10, CancellationToken cancellationToken = default)
    {
        var serializedObjects = new string[obj.Count];

        for (var i = 0; i < obj.Count; i++)
            serializedObjects[i] = messageSerializer.Serialize(obj[i]);

        return QueueBatchAsync(serializedObjects, queueName, delaySeconds, maxNumberOfMessagesForBatch, cancellationToken);
    }

    /// <inheritdoc/>
    private async Task QueueAsync(string serializedObject, string queueName, int delaySeconds = 0, CancellationToken cancellationToken = default)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = await sqsHelper.GetQueueUrlAsync(queueName),
            MessageBody = serializedObject,
            DelaySeconds = delaySeconds
        };

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Pushing message into SQS Queue: {QueueName} with this value {SerializedValue}", queueName, serializedObject);

        await SqsClient.SendMessageAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    private async Task QueueBatchAsync(IList<string> serializedObject, string queueName, int delaySeconds = 0, int maxNumberOfMessagesForBatch = 10, CancellationToken cancellationToken = default)
    {
        var requests = new SendMessageRequest[serializedObject.Count];
        var queueUrl = await sqsHelper.GetQueueUrlAsync(queueName);

        for (var i = 0; i < serializedObject.Count; i++)
        {
            requests[i] = new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = serializedObject[i],
                DelaySeconds = delaySeconds
            };
        }

        await QueueBatchAsync(requests, maxNumberOfMessagesForBatch, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task QueueAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        // This is needed in case the caller will add the queue name instead of queue url
        request.QueueUrl = await sqsHelper.GetQueueUrlAsync(request.QueueUrl);

        await SqsClient.SendMessageAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task QueueBatchAsync(IList<SendMessageRequest> requests, int maxNumberOfMessagesForBatch = 10, CancellationToken cancellationToken = default)
    {
        // Li gruppo per coda
        foreach (var group in requests.GroupBy(x => x.QueueUrl))
        {
            var queueUrl = group.First().QueueUrl;

            // Li gruppo per 10 che è il massimo numero di messaggi che si possono inviare in una singola richiesta
#if NET6_0 || NET7_0
            await Parallel.ForEachAsync(group.ToList().Split(maxNumberOfMessagesForBatch), cancellationToken, async (messages, token) =>
            {
                var entries = new List<SendMessageBatchRequestEntry>(maxNumberOfMessagesForBatch);

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
#else
            var groupedMessages = group.ToList().Split(maxNumberOfMessagesForBatch).ToList();

            var tasks = new Task[groupedMessages.Count];

            for (var i = 0; i < groupedMessages.Count; i++)
            {
                var messages = groupedMessages[i];
                var entries = new List<SendMessageBatchRequestEntry>(maxNumberOfMessagesForBatch);

                foreach (var message in messages)
                {
                    var entry = new SendMessageBatchRequestEntry();
                    entry.Id = Guid.NewGuid().ToString("N");
                    entry.MessageBody = message.MessageBody;
                    entry.DelaySeconds = message.DelaySeconds;
                    entries.Add(entry);
                }

                tasks[i] = SqsClient.SendMessageBatchAsync(queueUrl, entries, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
#endif
        }
    }
}
