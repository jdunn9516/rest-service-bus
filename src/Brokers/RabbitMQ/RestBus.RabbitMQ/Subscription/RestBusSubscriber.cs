using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Framing;
using RestBus.Common;
using RestBus.Common.Amqp;
using RestBus.RabbitMQ.ChannelPooling;
using RestBus.RabbitMQ.Consumer;
using System;
using System.Threading;

namespace RestBus.RabbitMQ.Subscription
{
    public class RestBusSubscriber : IRestBusSubscriber
    {
        //TODO: Error handling on the subscriber when the queue(s) expires

        IConnection conn;
        AmqpChannelPooler _subscriberPool;
        AmqpModelContainer workChannel;
        AmqpModelContainer subscriberChannel;
        ConcurrentQueueingConsumer workConsumer;
        ConcurrentQueueingConsumer subscriberConsumer;
        readonly ManualResetEventSlim requestQueued = new ManualResetEventSlim();
        readonly string subscriberId;
        readonly ExchangeInfo exchangeInfo;
        object exchangeDeclareSync = new object();
        InterlockedBoolean hasStarted;
        volatile bool disposed = false;
        readonly CancellationTokenSource disposedCancellationSource = new CancellationTokenSource();
        ConcurrentQueueingConsumer lastProcessedConsumerQueue = null;
        readonly ConnectionFactory connectionFactory;

        public RestBusSubscriber(IMessageMapper messageMapper )
        {
            exchangeInfo = messageMapper.GetExchangeInfo();
            subscriberId = AmqpUtils.GetNewExclusiveQueueId();

            this.connectionFactory = new ConnectionFactory();
            connectionFactory.Uri = exchangeInfo.ServerUris[0];
            connectionFactory.RequestedHeartbeat = Client.RestBusClient.HEART_BEAT;

            this.Settings = new SubscriberSettings(); //Make sure a default value is provided if not supplied by user.
        }

        public string Id
        {
            get { return subscriberId; }
        }

        public void Start()
        {
            if (!hasStarted.SetTrueIf(false))
            {
                throw new InvalidOperationException("RestBus Subscriber has already started!");
            }


            Restart();


        }

        public SubscriberSettings Settings { get; }

        internal bool HasStarted
        {
            get
            {
                return hasStarted;
            }
        }

        public void Restart()
        {
            hasStarted.Set(true);

            //CLose connections and channels
            if (subscriberChannel != null)
            {

                try
                {
                    subscriberChannel.Close();
                }
                catch 
                {
                }
            }

            if (workChannel != null)
            {
                try
                {
                    workChannel.Close();
                }
                catch
                {
                }
            }

            if (_subscriberPool != null)
            {
                _subscriberPool.Dispose();
            }

            if (conn != null)
            {
                try
                {
                    conn.Close();
                }
                catch
                {
                }

                try
                {
                    conn.Dispose();
                }
                catch 
                {
                }
            }


            //TODO: CreateConnection() can always throw BrokerUnreachableException so keep that in mind when calling
            conn = connectionFactory.CreateConnection();

            var pool = new AmqpChannelPooler(conn);
            Interlocked.Exchange(ref _subscriberPool, pool);

            //Use pool reference henceforth.

            //Create work channel and declare exchanges and queues
            Interlocked.Exchange(ref workChannel, pool.GetModel(ChannelFlags.Consumer));

            //TODO: Work this into subscriber dispose and restart
            /* Work this into subscriber dispose and restart
            //Cancel consumers on server
            if(workCTag != null)
            {
                try
                {
                    workChannel.BasicCancel(workCTag);
                }
                catch { }
            }

            if (subscriberCTag != null)
            {
                try
                {
                    workChannel.BasicCancel(subscriberCTag);
                }
                catch { }
            }
             */

            //Redeclare exchanges and queues
            AmqpUtils.DeclareExchangeAndQueues(workChannel.Channel, exchangeInfo, exchangeDeclareSync, subscriberId);

            //Listen on work queue
            Interlocked.Exchange(ref workConsumer, new ConcurrentQueueingConsumer(workChannel.Channel, requestQueued));
            string workQueueName = AmqpUtils.GetWorkQueueName(exchangeInfo);

            workChannel.Channel.BasicQos(0, 50, false);
            workChannel.Channel.BasicConsume(workQueueName, Settings.AckBehavior == SubscriberAckBehavior.Automatic, workConsumer);

            //Listen on subscriber queue
            Interlocked.Exchange(ref subscriberChannel, pool.GetModel(ChannelFlags.Consumer));
            Interlocked.Exchange(ref subscriberConsumer, new ConcurrentQueueingConsumer(subscriberChannel.Channel, requestQueued));
            string subscriberWorkQueueName = AmqpUtils.GetSubscriberQueueName(exchangeInfo, subscriberId);

            subscriberChannel.Channel.BasicQos(0, 50, false);
            subscriberChannel.Channel.BasicConsume(subscriberWorkQueueName, Settings.AckBehavior == SubscriberAckBehavior.Automatic, subscriberConsumer);
        }

        //Will block until a request is received from either queue
        public MessageContext Dequeue()
        {
            if (disposed) throw new ObjectDisposedException("Subscriber has been disposed");
            if(workConsumer == null || subscriberConsumer == null) throw new InvalidOperationException("Start the subscriber prior to calling Dequeue");

            //TODO: Test what happens if either of these consumers are cancelled by the server, should consumer.Cancelled be handled?

            HttpRequestPacket request;
            MessageDispatch dispatch;

            ConcurrentQueueingConsumer queue1 = null, queue2 = null;

            while (true)
            {
                if (disposed) throw new ObjectDisposedException("Subscriber has been disposed");
                if (lastProcessedConsumerQueue == subscriberConsumer)
                {
                    queue1 = workConsumer;
                    queue2 = subscriberConsumer;
                }
                else
                {
                    queue1 = subscriberConsumer;
                    queue2 = workConsumer;
                }

                try
                {
                    if (TryGetRequest(queue1, out request, out dispatch))
                    {
                        lastProcessedConsumerQueue = queue1;
                        break;
                    }

                    if (TryGetRequest(queue2, out request, out dispatch))
                    {
                        lastProcessedConsumerQueue = queue2;
                        break;
                    }
                }
                catch (Exception e)
                {
                    if (!(e is System.IO.EndOfStreamException))
                    {
                        //TODO: Log exception: Don't know what else to expect here

                    }

                    //TODO: IS this the best place to place reconnection logic? In a catch block??

                    //Loop until a connection is made
                    bool successfulRestart = false;
                    while (true)
                    {
                        try
                        {
                            Restart();
                            successfulRestart = true;
                        }
                        catch { }

                        if (disposed) throw new ObjectDisposedException("Subscriber has been disposed");

                        if (successfulRestart) break;
                        Thread.Sleep(1);
                    }

                    //Check for next message
                    continue;
                }

                //TODO: Combine CancellationToken passed in Dequeue() with token below 
                requestQueued.Wait(disposedCancellationSource.Token);
                requestQueued.Reset();
            }

            return new MessageContext
            {
                Request = request,
                ReplyToQueue = dispatch.Delivery.BasicProperties == null ? null : dispatch.Delivery.BasicProperties.ReplyTo,
                CorrelationId = dispatch.Delivery.BasicProperties.CorrelationId,
                Dispatch = dispatch
            };


        }

        private bool TryGetRequest(ConcurrentQueueingConsumer consumer, out HttpRequestPacket request, out MessageDispatch dispatch)
        {
            request = null;
            dispatch = null;

            BasicDeliverEventArgs item;
            if (!consumer.TryInstantDequeue(out item))
            {
                return false;
            }

            //TODO: Pool MessageDispatch
            //Get message 
            dispatch = new MessageDispatch { Consumer = consumer, Delivery = item };

            //Deserialize message
            bool wasDeserialized = true;

            try
            {
                request = HttpRequestPacket.Deserialize(item.Body);

                //Add/Update Subscriber-Id header
                request.Headers[Common.Shared.SUBSCRIBER_ID_HEADER] = new string[] { this.subscriberId };

                //Add redelivered header if item was redelivered.
                if(item.Redelivered)
                {
                    request.Headers[Common.Shared.REDELIVERED_HEADER] = new string[] { true.ToString() };
                }

            }
            catch
            {
                wasDeserialized = false;
            }

            //Reject message if deserialization failed.
            if (!wasDeserialized && Settings.AckBehavior != SubscriberAckBehavior.Automatic )
            {
                consumer.Model.BasicReject(item.DeliveryTag, false);
                return false;
            }

            return true;

        }

        public void Dispose()
        {
            disposed = true;
            disposedCancellationSource.Cancel();

            if (workChannel != null)
            {
                workChannel.Close();
            }

            if (subscriberChannel != null)
            {
                subscriberChannel.Close();
            }

            if (_subscriberPool != null)
            {
                _subscriberPool.Dispose();
            }

            if (conn != null)
            {
                try
                {
                    conn.Close();
                }
                catch { }

                try
                {
                    conn.Dispose();
                }
                catch { }
            }

            requestQueued.Dispose();
            disposedCancellationSource.Dispose();

        }

        public void SendResponse(MessageContext context, HttpResponsePacket response )
        {
            if (disposed) throw new ObjectDisposedException("Subscriber has been disposed");

            var dispatch = context.Dispatch as MessageDispatch;
            if (dispatch != null)
            {
                //Ack request
                if(Settings.AckBehavior != SubscriberAckBehavior.Automatic && dispatch.Consumer.Model.IsOpen)
                {
                    dispatch.Consumer.Model.BasicAck(dispatch.Delivery.DeliveryTag, false);
                }
            }

            //Exit method if no replyToQueue was specified.
            if (String.IsNullOrEmpty(context.ReplyToQueue)) return;

            if (conn == null)
            {
                //TODO: Log this -- it technically shouldn't happen. Also translate to a HTTP Unreachable because it means StartCallbackQueueConsumer didn't create a connection
                throw new ApplicationException("This is Bad");
            }

            var pooler = _subscriberPool;
            AmqpModelContainer model = null;
            try
            {
                model = pooler.GetModel(ChannelFlags.None);
                BasicProperties basicProperties = new BasicProperties { CorrelationId = context.CorrelationId };

                //TODO: Add Subscriber Id header to reponse before sending it

                    model.Channel.BasicPublish(String.Empty,
                                    context.ReplyToQueue,
                                    basicProperties,
                                    response.Serialize());
            }
            finally
            {
                if(model != null)
                {
                    model.Close();
                }
            }

        }

    }
}
