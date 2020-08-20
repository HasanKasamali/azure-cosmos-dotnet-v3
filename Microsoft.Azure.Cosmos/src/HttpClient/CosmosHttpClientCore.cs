﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal class CosmosHttpClientCore : CosmosHttpClient
    {
        private const int BackOffMultiplier = 2;
        private static readonly TimeSpan GatewayRequestTimeout = TimeSpan.FromSeconds(65);
        private readonly HttpClient httpClient;
        private readonly ICommunicationEventSource eventSource;

        private bool disposedValue;

        public override HttpMessageHandler HttpMessageHandler { get; }

        private CosmosHttpClientCore(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            ICommunicationEventSource eventSource)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
            this.HttpMessageHandler = httpMessageHandler;
        }

        public static CosmosHttpClient CreateWithConnectionPolicy(
            ApiType apiType,
            ICommunicationEventSource eventSource,
            ConnectionPolicy connectionPolicy,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            Func<HttpClient> httpClientFactory = connectionPolicy.HttpClientFactory;
            if (httpClientFactory != null)
            {
                if (sendingRequestEventArgs != null &&
                    receivedResponseEventArgs != null)
                {
                    throw new InvalidOperationException($"{nameof(connectionPolicy.HttpClientFactory)} can not be set at the same time as {nameof(sendingRequestEventArgs)} or {nameof(ReceivedResponseEventArgs)}");
                }

                return Create(
                    connectionPolicy.RequestTimeout,
                    connectionPolicy.UserAgentContainer,
                    apiType,
                    eventSource,
                    connectionPolicy.HttpClientFactory);
            }

            return Create(
                requestTimeout: connectionPolicy.RequestTimeout,
                userAgentContainer: connectionPolicy.UserAgentContainer,
                apiType: apiType,
                eventSource: eventSource,
                gatewayModeMaxConnectionLimit: connectionPolicy.MaxConnectionLimit,
                webProxy: null,
                sendingRequestEventArgs: sendingRequestEventArgs,
                receivedResponseEventArgs: receivedResponseEventArgs);
        }

        public static CosmosHttpClient CreateWithClientOptions(
            ApiType apiType,
            ICommunicationEventSource eventSource,
            UserAgentContainer userAgentContainer,
            CosmosClientOptions clientOptions)
        {
            if (clientOptions.HttpClientFactory != null &&
                clientOptions.SendingRequestEventArgs != null)
            {
                throw new NotSupportedException("HttpClientFactory and SendingRequestEventArgs can not be used at the same time. Please add SendingRequestEventArgs as part of the HttpClientFactory.");
            }

            if (clientOptions.HttpClientFactory != null &&
                clientOptions.WebProxy != null)
            {
                throw new NotSupportedException("HttpClientFactory and WebProxy can not be used at the same time. Please add WebProxy as part of the HttpClientFactory.");
            }

            if (clientOptions.HttpClientFactory != null)
            {
                return Create(
                    clientOptions.RequestTimeout,
                    userAgentContainer,
                    apiType,
                    eventSource,
                    clientOptions.HttpClientFactory);
            }

            return Create(
                requestTimeout: clientOptions.RequestTimeout,
                userAgentContainer: userAgentContainer,
                apiType: apiType,
                eventSource: eventSource,
                gatewayModeMaxConnectionLimit: clientOptions.GatewayModeMaxConnectionLimit,
                webProxy: clientOptions.WebProxy,
                sendingRequestEventArgs: clientOptions.SendingRequestEventArgs,
                receivedResponseEventArgs: null);
        }

        internal static CosmosHttpClient Create(
            TimeSpan requestTimeout,
            UserAgentContainer userAgentContainer,
            ApiType apiType,
            ICommunicationEventSource eventSource,
            int gatewayModeMaxConnectionLimit,
            IWebProxy webProxy,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            // https://docs.microsoft.com/en-us/archive/blogs/timomta/controlling-the-number-of-outgoing-connections-from-httpclient-net-core-or-full-framework
            HttpMessageHandler httpMessageHandler = new HttpClientHandler
            {
                Proxy = webProxy,
                MaxConnectionsPerServer = gatewayModeMaxConnectionLimit
            };

            if (sendingRequestEventArgs != null
                || receivedResponseEventArgs != null)
            {
                httpMessageHandler = new HttpRequestMessageHandler(
                    sendingRequestEventArgs,
                    receivedResponseEventArgs,
                    httpMessageHandler);
            }

            HttpClient httpClient = new HttpClient(httpMessageHandler);
            return CreateHelper(
               httpClient,
               httpMessageHandler,
               requestTimeout,
               userAgentContainer,
               apiType,
               eventSource);
        }

        internal static CosmosHttpClient Create(
            TimeSpan requestTimeout,
            UserAgentContainer userAgentContainer,
            ApiType apiType,
            ICommunicationEventSource eventSource,
            Func<HttpClient> httpFactory)
        {
            HttpClient httpClient = httpFactory?.Invoke() ?? throw new ArgumentNullException(nameof(httpFactory));
            return CreateHelper(
                httpClient,
                httpMessageHandler: null,
                requestTimeout,
                userAgentContainer,
                apiType,
                eventSource);
        }

        private static CosmosHttpClient CreateHelper(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            TimeSpan requestTimeout,
            UserAgentContainer userAgentContainer,
            ApiType apiType,
            ICommunicationEventSource eventSource)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            httpClient.Timeout = requestTimeout > CosmosHttpClientCore.GatewayRequestTimeout
                ? requestTimeout
                : CosmosHttpClientCore.GatewayRequestTimeout;
            httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            httpClient.AddUserAgentHeader(userAgentContainer);
            httpClient.AddApiTypeHeader(apiType);

            // Set requested API version header that can be used for
            // version enforcement.
            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Version,
                HttpConstants.Versions.CurrentVersion);

            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Accept, RuntimeConstants.MediaTypes.Json);

            return new CosmosHttpClientCore(
                httpClient,
                httpMessageHandler,
                eventSource);
        }

        public override Task<HttpResponseMessage> GetAsync(
            Uri uri,
            INameValueCollection additionalHeaders,
            ResourceType resourceType,
            CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            // GetAsync doesn't let clients to pass in additional headers. So, we are
            // internally using SendAsync and add the additional headers to requestMessage. 
            ValueTask<HttpRequestMessage> CreateRequestMessage()
            {
                HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                if (additionalHeaders != null)
                {
                    foreach (string header in additionalHeaders)
                    {
                        if (GatewayStoreClient.IsAllowedRequestHeader(header))
                        {
                            requestMessage.Headers.TryAddWithoutValidation(header, additionalHeaders[header]);
                        }
                    }
                }

                return new ValueTask<HttpRequestMessage>(requestMessage);
            }

            return this.SendHttpAsync(
                CreateRequestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                resourceType,
                cancellationToken);
        }

        public override Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessage,
            ResourceType resourceType,
            CancellationToken cancellationToken)
        {
            return this.SendHttpAsync(
                createRequestMessage,
                HttpCompletionOption.ResponseContentRead,
                resourceType,
                cancellationToken);
        }

        private async Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessage,
            HttpCompletionOption httpCompletionOption,
            ResourceType resourceType,
            CancellationToken cancellationToken)
        {
            int attemptCount = 0;
            TimeSpan currentBackOffSeconds = TimeSpan.FromSeconds(1);
            DateTime startTimeUtc = DateTime.UtcNow;

            while (true)
            {
                using (HttpRequestMessage requestMessage = await createRequestMessage())
                {
                    DateTime sendTimeUtc = DateTime.UtcNow;
                    Guid localGuid = Guid.NewGuid(); // For correlating HttpRequest and HttpResponse Traces

                    Guid requestedActivityId = Trace.CorrelationManager.ActivityId;
                    this.eventSource.Request(requestedActivityId, localGuid, requestMessage.RequestUri.ToString(),
                        resourceType.ToResourceTypeString(), requestMessage.Headers);

                    TimeSpan durationTimeSpan;
                    Exception retryException;
                    try
                    {
                        HttpResponseMessage responseMessage =
                            await this.httpClient.SendAsync(requestMessage, httpCompletionOption, cancellationToken);

                        DateTime receivedTimeUtc = DateTime.UtcNow;
                        durationTimeSpan = receivedTimeUtc - sendTimeUtc;

                        Guid activityId = Guid.Empty;
                        if (responseMessage.Headers.TryGetValues(HttpConstants.HttpHeaders.ActivityId,
                            out IEnumerable<string> headerValues) && headerValues.Any())
                        {
                            activityId = new Guid(headerValues.First());
                        }

                        this.eventSource.Response(
                            activityId,
                            localGuid,
                            (short)responseMessage.StatusCode,
                            durationTimeSpan.TotalMilliseconds,
                            responseMessage.Headers);

                        return responseMessage;
                    }
                    // HttpClient can throw operation canceled exceptions. Only do retries when the user did not cancel the operation.
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // throw timeout if the cancellationToken is not canceled (i.e. httpClient timed out)
                        durationTimeSpan = DateTime.UtcNow - sendTimeUtc;
                        string message =
                            $"GatewayStoreClient Request Timeout. Start Time:{sendTimeUtc}; Total Duration:{durationTimeSpan}; Http Client Timeout:{this.httpClient.Timeout}; Activity id: {requestedActivityId};";
                        retryException = CosmosExceptionFactory.CreateRequestTimeoutException(
                            message,
                            innerException: ex);
                    }
                    // Retry all web exceptions on get as there is no possible conflicts or race conditions
                    catch (WebException webException) when (requestMessage.Method == HttpMethod.Get || WebExceptionUtility.IsWebExceptionRetriable(webException))
                    {
                        retryException = webException;
                    }

                    // Don't penalize first retry with delay.
                    if (attemptCount++ > 1)
                    {
                        TimeSpan totalDurationWithRetries = DateTime.UtcNow - startTimeUtc;
                        TimeSpan remainingSeconds = CosmosHttpClientCore.GatewayRequestTimeout - totalDurationWithRetries;
                        if (remainingSeconds <= TimeSpan.Zero)
                        {
                            throw retryException;
                        }

                        TimeSpan backOffTime = currentBackOffSeconds < remainingSeconds
                            ? currentBackOffSeconds
                            : remainingSeconds;
                        
                        currentBackOffSeconds = TimeSpan.FromSeconds(currentBackOffSeconds.TotalSeconds * CosmosHttpClientCore.BackOffMultiplier);
                        await Task.Delay(backOffTime, cancellationToken);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.httpClient.Dispose();
                }

                this.disposedValue = true;
            }
        }

        public override void Dispose()
        {
            this.Dispose(true);
        }

        private class HttpRequestMessageHandler : DelegatingHandler
        {
            private readonly EventHandler<SendingRequestEventArgs> sendingRequest;
            private readonly EventHandler<ReceivedResponseEventArgs> receivedResponse;

            public HttpRequestMessageHandler(
                EventHandler<SendingRequestEventArgs> sendingRequest,
                EventHandler<ReceivedResponseEventArgs> receivedResponse,
                HttpMessageHandler innerHandler)
            {
                this.sendingRequest = sendingRequest;
                this.receivedResponse = receivedResponse;

                this.InnerHandler = innerHandler ?? new HttpClientHandler();
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.sendingRequest?.Invoke(this, new SendingRequestEventArgs(request));
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                this.receivedResponse?.Invoke(this, new ReceivedResponseEventArgs(request, response));
                return response;
            }
        }
    }
}