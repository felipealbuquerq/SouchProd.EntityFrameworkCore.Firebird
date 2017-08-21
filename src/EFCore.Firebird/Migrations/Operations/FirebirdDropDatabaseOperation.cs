// Copyright (c) SouchProd. All rights reserved. // TODO: Credits Pomelo Foundation & EFCore
// Licensed under the MIT. See LICENSE in the project root for license information.

using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Migrations.Operations
{
    public class FirebirdDropDatabaseOperation : MigrationOperation
    {
        public virtual string Name { get;[param: NotNull] set; }
    }
}