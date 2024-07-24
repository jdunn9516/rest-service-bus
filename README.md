## Easy, Service Oriented, Asynchronous Messaging and Queueing for .NET ##

RestBus is a high performance library for RabbitMQ that lets you consume ASP.NET Core (ASP.NET 5), Web API and ServiceStack service endpoints via RabbitMQ.

Sending a message is as easy as:

```csharp
var amqpUrl = "amqp://localhost:5672"; //AMQP URI for RabbitMQ server
var serviceName = "samba"; //The unique identifier for the target service

var client = new RestBusClient(new BasicMessageMapper(amqpUrl, serviceName));

//Call the /hello/random endpoint
var response = await client.GetAsync("/hello/random");
```

where `/hello/random` is an ordinary web service endpoint in an ASP.NET Core, Web API or ServiceStack service.  
RestBus routes the request over RabbitMQ, invokes the endpoint and returns the response, without ever hitting the HTTP transport.

### License

Apache License, Version 2.0
