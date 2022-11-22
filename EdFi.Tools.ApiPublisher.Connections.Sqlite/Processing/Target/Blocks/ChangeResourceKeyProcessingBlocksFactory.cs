// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;

public class ChangeResourceKeyProcessingBlocksFactory : SqlLiteProcessingBlocksFactoryBase<KeyChangesJsonMessage>
{
    public ChangeResourceKeyProcessingBlocksFactory(Func<SqliteConnection> createConnection)
        : base(createConnection) { }

    protected override string TableSuffix
    {
        get => "KeyChanges";
    }
}
