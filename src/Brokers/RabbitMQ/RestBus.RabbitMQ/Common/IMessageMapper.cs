using System.Net.Http;
namespace RestBus.RabbitMQ
{
    public interface IMessageMapper
    {
        ExchangeInfo GetExchangeInfo();
        string GetRoutingKey(HttpRequestMessage request);
        bool GetExpires(HttpRequestMessage request);
    }
}
