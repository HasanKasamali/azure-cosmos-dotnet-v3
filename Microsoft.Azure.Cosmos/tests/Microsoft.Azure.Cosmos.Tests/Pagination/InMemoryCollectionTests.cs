﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests.Pagination
{
    using Microsoft.Azure.Cosmos.Pagination;
    using Microsoft.Azure.Documents;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public sealed class InMemoryCollectionTests : DocumentContainerTests
    {
        internal override IDocumentContainer CreateDocumentContainer(
            PartitionKeyDefinition partitionKeyDefinition,
            FlakyDocumentContainer.FailureConfigs failureConfigs = null) => new DocumentContainer(
                new FlakyDocumentContainer(
                    new InMemoryContainer(partitionKeyDefinition),
                    failureConfigs));
    }
}