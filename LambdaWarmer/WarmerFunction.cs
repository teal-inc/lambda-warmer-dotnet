﻿using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.Serialization.SystemTextJson.Converters;

namespace LambdaWarmer;

public abstract class WarmerFunction<TRequest, TResponse>
{
    private readonly IAmazonLambda _lambda;

    private readonly WarmerConfig _config;
    private bool _warm;
    private DateTimeOffset? _lastAccess;

    protected WarmerFunction(WarmerConfig? config = null, IAmazonLambda? lambda = null)
    {
        _config = config ?? new WarmerConfig();
        SerializerOptions = CreateDefaultJsonSerializationOptions();
        _lambda = lambda ?? new AmazonLambdaClient();
    }

    // ReSharper disable once UnusedMember.Global
    public async Task<Stream> FunctionHandlerAsync(Stream requestStream, ILambdaContext context)
    {
        var warmerEvent = await DeserializeAsync(requestStream);
        if (warmerEvent.Warmer)
        {
            await WarmUpAsync(warmerEvent, context);
            return Stream.Null;
        }

        _warm = true;
        _lastAccess = DateTimeOffset.UtcNow;
        var response = await InternalFunctionHandlerAsync(warmerEvent.Request, context);
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        return new MemoryStream(Encoding.Default.GetBytes(json));
    }

    private async Task WarmUpAsync(WarmerEvent warmerEvent, ILambdaContext context)
    {
        var concurrency = warmerEvent.Concurrency > 1 ? warmerEvent.Concurrency : 1;
        var invocationNumber = warmerEvent.InvocationNumber > 0 ? warmerEvent.InvocationNumber : 1;
        var totalInvocation = warmerEvent.TotalInvocation > 0 ? warmerEvent.TotalInvocation : concurrency;
        var correlationId = warmerEvent.CorrelationId ?? context.AwsRequestId;

        if (_config.Log)
        {
            Log(context, correlationId, invocationNumber, totalInvocation);
        }

        _warm = true;
        
        await Task.Delay(_config.Delay);

        if (concurrency == 1)
        {
            await InternalWarmUpAsync(context);
            return;
        }

        await InvokeConcurrency(context, concurrency, correlationId);
    }

    private async Task InvokeConcurrency(ILambdaContext context, int concurrency, string correlationId)
    {
        var tasks = new List<Task>
        {
            InternalWarmUpAsync(context)
        };

        for (var i = 2; i <= concurrency; i++)
        {
            var invokeRequest = new InvokeRequest
            {
                FunctionName = context.InvokedFunctionArn,
                InvocationType = i == concurrency
                    ? InvocationType.RequestResponse
                    : InvocationType.Event,
                LogType = LogType.None,
                Payload = JsonSerializer.Serialize(new WarmerEvent
                {
                    Warmer = true,
                    InvocationNumber = i,
                    TotalInvocation = concurrency,
                    CorrelationId = correlationId
                })
            };
                
            tasks.Add(_lambda.InvokeAsync(invokeRequest));
        }

        await Task.WhenAll(tasks);
    }

    private void Log(ILambdaContext context, string correlationId, int invocationNumber, int totalInvocation)
    {
        var log = new
        {
            action = "warmer",
            function = context.InvokedFunctionArn,
            correlationId,
            count = invocationNumber,
            concurrency = totalInvocation,
            warm = _warm,
            lastAccessed = _lastAccess.ToString(),
            lastAccessedSeconds = _lastAccess is null
                ? null as int?
                : (int)(DateTimeOffset.UtcNow - _lastAccess.Value).TotalSeconds,
        };
        context.Logger.LogInformation(JsonSerializer.Serialize(log));
    }

    #region Abstract members

    // ReSharper disable once MemberCanBeProtected.Global
    public abstract Task InternalWarmUpAsync(ILambdaContext context);

    // ReSharper disable once MemberCanBeProtected.Global
    public abstract Task<TResponse> InternalFunctionHandlerAsync(TRequest request, ILambdaContext context);

    #endregion
    
    #region Serialization

    private JsonSerializerOptions SerializerOptions { get; }

    private static JsonSerializerOptions CreateDefaultJsonSerializationOptions()
    {
        var serializationOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = new AwsNamingPolicy()
        };
        serializationOptions.Converters.Add(new DateTimeConverter());
        serializationOptions.Converters.Add(new MemoryStreamConverter());
        serializationOptions.Converters.Add(new ConstantClassConverter());
        serializationOptions.Converters.Add(new ByteArrayConverter());
        return serializationOptions;
    }

    private async Task<WarmerEvent<TRequest>> DeserializeAsync(Stream stream)
    {
        using var textReader = new StreamReader(stream);
        var stringContent = await textReader.ReadToEndAsync();
        var warmerEvent = JsonSerializer.Deserialize<WarmerEvent<TRequest>>(stringContent, SerializerOptions)!;
        warmerEvent.Request = JsonSerializer.Deserialize<TRequest>(stringContent, SerializerOptions)!;
        return warmerEvent;
    }

    #endregion
}