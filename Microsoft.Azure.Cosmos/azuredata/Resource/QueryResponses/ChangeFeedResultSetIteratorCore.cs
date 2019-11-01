//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Azure.Cosmos
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Query;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Cosmos Stand-By Feed iterator implementing Composite Continuation Token
    /// </summary>
    internal class ChangeFeedResultSetIteratorCore : FeedIterator
    {
        internal StandByFeedContinuationToken compositeContinuationToken;

        private readonly CosmosClientContext clientContext;
        private readonly ContainerCore container;
        private string containerRid;
        private string continuationToken;
        private int? maxItemCount;

        internal ChangeFeedResultSetIteratorCore(
            CosmosClientContext clientContext,
            ContainerCore container,
            string continuationToken,
            int? maxItemCount,
            ChangeFeedRequestOptions options)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));

            this.clientContext = clientContext;
            this.container = container;
            this.changeFeedOptions = options;
            this.maxItemCount = maxItemCount;
            this.continuationToken = continuationToken;
        }

        /// <summary>
        /// The query options for the result set
        /// </summary>
        protected readonly ChangeFeedRequestOptions changeFeedOptions;

        public override bool HasMoreResults => true;

        /// <summary>
        /// Get the next set of results from the cosmos service
        /// </summary>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>A query response from cosmos service</returns>
        public override async Task<Response> ReadNextAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            string firstNotModifiedKeyRangeId = null;
            string currentKeyRangeId;
            string nextKeyRangeId;
            ResponseMessage response;
            do
            {
                (currentKeyRangeId, response) = await this.ReadNextInternalAsync(cancellationToken);
                if (response.Status != (int)HttpStatusCode.NotModified)
                {
                    break;
                }

                // HttpStatusCode.NotModified
                if (string.IsNullOrEmpty(firstNotModifiedKeyRangeId))
                {
                    // First NotModified Response
                    firstNotModifiedKeyRangeId = currentKeyRangeId;
                }

                // Current Range is done, push it to the end
                this.compositeContinuationToken.MoveToNextToken();
                (_, nextKeyRangeId) = await this.compositeContinuationToken.GetCurrentTokenAsync();
            }
            // We need to keep checking across all ranges until one of them returns OK or we circle back to the start
            while (!firstNotModifiedKeyRangeId.Equals(nextKeyRangeId, StringComparison.InvariantCultureIgnoreCase));

            // Send to the user the composite state for all ranges
            response.CosmosHeaders.ContinuationToken = this.compositeContinuationToken.ToString();
            return response;
        }

        internal async Task<Tuple<string, ResponseMessage>> ReadNextInternalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.compositeContinuationToken == null)
            {
                PartitionKeyRangeCache pkRangeCache = await this.clientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
                this.containerRid = await this.container.GetRIDAsync(cancellationToken);
                this.compositeContinuationToken = await StandByFeedContinuationToken.CreateAsync(this.containerRid, this.continuationToken, pkRangeCache.TryGetOverlappingRangesAsync);
            }

            (CompositeContinuationToken currentRangeToken, string rangeId) = await this.compositeContinuationToken.GetCurrentTokenAsync();
            string partitionKeyRangeId = rangeId;
            this.continuationToken = currentRangeToken.Token;
            ResponseMessage response = await this.NextResultSetDelegateAsync(this.continuationToken, partitionKeyRangeId, this.maxItemCount, this.changeFeedOptions, cancellationToken);
            if (await this.ShouldRetryFailureAsync(response, cancellationToken))
            {
                return await this.ReadNextInternalAsync(cancellationToken);
            }

            if (response.IsSuccessStatusCode
                || response.Status == (int)HttpStatusCode.NotModified)
            {
                // Change Feed read uses Etag for continuation
                currentRangeToken.Token = response.CosmosHeaders.ETag;
            }

            return new Tuple<string, ResponseMessage>(partitionKeyRangeId, response);
        }

        /// <summary>
        /// During Feed read, split can happen or Max Item count can go beyond the max response size
        /// </summary>
        internal async Task<bool> ShouldRetryFailureAsync(
            ResponseMessage response,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            bool partitionSplit = response.StatusCode == HttpStatusCode.Gone
                && (response.CosmosHeaders.SubStatusCode == SubStatusCodes.PartitionKeyRangeGone || response.CosmosHeaders.SubStatusCode == SubStatusCodes.CompletingSplit);
            if (partitionSplit)
            {
                // Forcing stale refresh of Partition Key Ranges Cache
                await this.compositeContinuationToken.GetCurrentTokenAsync(forceRefresh: true);
                return true;
            }

            return false;
        }

        internal virtual async Task<ResponseMessage> NextResultSetDelegateAsync(
            string continuationToken,
            string partitionKeyRangeId,
            int? maxItemCount,
            ChangeFeedRequestOptions options,
            CancellationToken cancellationToken)
        {
            Uri resourceUri = this.container.LinkUri;
            Response response = await this.clientContext.ProcessResourceOperationStreamAsync(
                resourceUri: resourceUri,
                resourceType: ResourceType.Document,
                operationType: OperationType.ReadFeed,
                requestOptions: options,
                cosmosContainerCore: this.container,
                requestEnricher: request =>
                {
                    ChangeFeedRequestOptions.FillContinuationToken(request, continuationToken);
                    ChangeFeedRequestOptions.FillMaxItemCount(request, maxItemCount);
                    ChangeFeedRequestOptions.FillPartitionKeyRangeId(request, partitionKeyRangeId);
                },
                partitionKey: null,
                streamPayload: null,
                cancellationToken: cancellationToken);

            return response as ResponseMessage;
        }
    }
}